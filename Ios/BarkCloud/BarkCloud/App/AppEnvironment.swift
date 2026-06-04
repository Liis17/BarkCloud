import Foundation
import Observation
import SwiftData
import BarkCloudKit

@MainActor
@Observable
final class AppEnvironment {
    let serverConfig: ServerConfigStore
    let sessionStore: SessionStore
    let grpcManager: GrpcManager
    let authRepository: AuthRepository
    let localFileRepository: LocalFileRepository
    let fileTransfer: FileTransferService
    let userRepository: UserRepository
    let cloudRepository: CloudRepository
    let albumRepository: AlbumRepository
    let fileCache: FileCacheService
    let fileCacheSettings: FileCacheSettings
    let autoUploadSettings: AutoUploadSettings
    let backupManager: BackupManager
    let languageSettings: LanguageSettings
    let language: LanguageManager
    let vault: VaultStore
    let biometric: BiometricGate
    let appLockSettings: AppLockSettings
    let appLock: AppLockManager
    let shareInboxUploader: ShareInboxUploader
    let backgroundUploads: BackgroundUploadCoordinator
    /// Источник истины для глобального баннера прогресса над TabBar (см.
    /// [[GlobalUploadBanner]]). Подписан на координатор через `addObserver`.
    let uploadProgress: UploadProgressObserver

    init() {
        self.serverConfig = ServerConfigStore()

        let langSettings = LanguageSettings()
        self.languageSettings = langSettings
        self.language = LanguageManager(settings: langSettings)

        let session = SessionStore()
        let grpc = GrpcManager(session: session)
        let transfer = FileTransferService(grpc: grpc)

        self.sessionStore = session
        self.grpcManager = grpc
        self.authRepository = AuthRepository(grpc: grpc, session: session)
        self.localFileRepository = LocalFileRepository()
        self.fileTransfer = transfer
        self.userRepository = UserRepository(grpc: grpc, transfer: transfer)
        self.cloudRepository = CloudRepository(grpc: grpc, transfer: transfer)
        self.albumRepository = AlbumRepository(grpc: grpc)

        let cacheSettings = FileCacheSettings()
        let cache = FileCacheService(
            modelContainer: Self.makeCacheContainer(),
            settings: cacheSettings,
            http: InsecureHTTP.session
        )
        self.fileCacheSettings = cacheSettings
        self.fileCache = cache

        let autoUpload = AutoUploadSettings()
        self.autoUploadSettings = autoUpload
        self.backupManager = BackupManager(cloud: self.cloudRepository, settings: autoUpload)

        Task { await cache.runStartupSweepIfNeeded() }
        // Если автозагрузка включена — продолжить скан/докачку на переднем плане.
        backupManager.resumeIfEnabled()
        self.vault = VaultStore()
        self.biometric = BiometricGate()
        let lockSettings = AppLockSettings()
        let lock = AppLockManager(settings: lockSettings, biometric: self.biometric)
        self.appLockSettings = lockSettings
        self.appLock = lock
        self.shareInboxUploader = ShareInboxUploader(cloud: self.cloudRepository, session: session)

        // Координатор фоновой загрузки. Singleton — той же URLSession касается и
        // Share Extension (через тот же `identifier`). Здесь конфигурируем хуки.
        let uploads = BackgroundUploadCoordinator.shared
        self.backgroundUploads = uploads
        let transferRef = self.fileTransfer
        let cloudRef = self.cloudRepository
        uploads.tokenProvider = { [weak transferRef] in
            await transferRef?.validAccessToken()
        }
        uploads.onPersistentFailure = {
            scheduleRetryBGTaskIfNeeded()
        }
        // Системный observer: при completed — привязать файл к каталогу. Через
        // addObserver, чтобы UI-наблюдатели (UploadProgressObserver) могли
        // подписаться независимо, не перетирая друг друга.
        //
        // Куда привязывать:
        // - `.backup` (автозагрузка медиатеки) и `.share` без выбранной папки —
        //   по типу медиа: сервер сам кладёт в «Фото»/«Видео»/«Другие документы»
        //   (`route_by_media_kind`). Папка «Недавно загруженные» больше не нужна.
        // - всё остальное (ручная загрузка в Cloud Browser, шаринг с выбранной
        //   папкой) — в конкретный `directoryID`.
        uploads.addObserver(completion: { snapshot in
            Task { [weak cloudRef] in
                guard let cloudRef, !snapshot.preparedFileID.isEmpty else { return }
                let directoryID = snapshot.directoryID ?? ""
                let routeByMediaKind = snapshot.source == .backup
                    || (snapshot.source == .share && directoryID.isEmpty)
                if routeByMediaKind {
                    try? await cloudRef.attachFile(
                        fileID: snapshot.preparedFileID,
                        directoryID: "",
                        name: snapshot.fileName,
                        routeByMediaKind: true
                    )
                } else if !directoryID.isEmpty {
                    try? await cloudRef.attachFile(
                        fileID: snapshot.preparedFileID,
                        directoryID: directoryID,
                        name: snapshot.fileName
                    )
                }
            }
        })

        // Глобальный баннер прогресса над TabBar.
        let progress = UploadProgressObserver(queueStore: .shared, backupManager: self.backupManager)
        self.uploadProgress = progress
        progress.attach(to: uploads)

        // Полная очистка при исчерпании попыток PIN.
        lock.onWipe = { [weak self] in
            guard let self else { return }
            await self.resetLocalState()
        }

        Task { await cache.runStartupSweepIfNeeded() }
        // Догрузить то, что Share Extension сложил в общий контейнер.
        shareInboxUploader.uploadPendingIfNeeded()
        // Прицепиться к существующей background-сессии: подобрать недозавершённые
        // jobs (running без живой task) — это случается после kill main app.
        Task { await uploads.attachAndResubmitOrphans() }
    }

    /// Контейнер SwiftData для метаданных кеша (`BarkCloudCache.sqlite` в Application
    /// Support). При сбое открытия БД откатываемся на in-memory, чтобы не уронить старт.
    private static func makeCacheContainer() -> ModelContainer {
        let fm = FileManager.default
        let appSupport = URL.applicationSupportDirectory
        try? fm.createDirectory(at: appSupport, withIntermediateDirectories: true)
        let storeURL = appSupport.appendingPathComponent("BarkCloudCache.sqlite")
        do {
            return try ModelContainer(
                for: CachedFileEntry.self,
                configurations: ModelConfiguration(url: storeURL)
            )
        } catch {
            return try! ModelContainer(
                for: CachedFileEntry.self,
                configurations: ModelConfiguration(isStoredInMemoryOnly: true)
            )
        }
    }

    /// Полный выход из аккаунта: серверный отзыв сессии (best-effort) с последующей
    /// полной локальной очисткой. Порядок важен — отзыв сессии использует ещё
    /// действующий токен, поэтому идёт до очистки.
    func signOut() async {
        await authRepository.logout()
        await resetLocalState()
    }

    /// Полный сброс локального состояния до «свежей установки»: токены в Keychain,
    /// кэшированные gRPC-соединения, очередь и live-задачи фоновой загрузки, кеши
    /// (файлы, изображения, URL, хеши ассетов), настройки автозагрузки и кеша,
    /// блокировка приложения (PIN/Face ID), локальный «сейф» и адреса сервера.
    /// После сброса `RootView` показывает экран ввода адресов сервера. Используется
    /// при выходе, удалении аккаунта и принудительном wipe по неверному PIN.
    func resetLocalState() async {
        sessionStore.clearSession()
        await grpcManager.shutdown()
        await backgroundUploads.cancelAll()
        await UploadQueueStore.shared.deleteAll()
        UploadConstants.purgeStaging()
        backupManager.setAutoUpload(false)
        RemoteImageCache.shared.clear()
        InsecureHTTP.clearCaches()
        await fileCache.clearAll()
        await AssetHashStore.shared.clearAll()
        await CloudDeviceLinkStore.shared.clearAll()
        fileCacheSettings.reset()
        appLockSettings.disable()
        vault.removeAll()
        language.reset()
        serverConfig.reset()
    }
}
