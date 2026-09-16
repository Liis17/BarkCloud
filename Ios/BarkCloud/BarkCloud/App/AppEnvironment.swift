import Foundation
import Observation
import SwiftData
import GRPCCore
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
    let dynamicFolderRepository: DynamicFolderRepository
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

    /// Ожидающая обработки глубокая ссылка (тап по виджету). `RootView` пишет сюда
    /// в `onOpenURL`, `MainScreen` читает, переключает таб и обнуляет.
    var pendingDeepLink: DeepLink?
    /// Запрос показать `VaultScreen` поверх таба «Настройки» (для `barkcloud://vault`).
    var presentVault = false
    /// `file_id` фото, которое нужно открыть в пейджере на вкладке «Альбомы» (для
    /// `barkcloud://media/<id>`). Сетка фото подхватывает и обнуляет (consume-once).
    var pendingMediaID: String?

    init() {
        // `tmp` содержит только воспроизводимые временные файлы; после краша или
        // force-quit они могут остаться и больше не попасть под очистку UI.
        TemporaryFileCleanup.purgeStale()
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
        self.dynamicFolderRepository = DynamicFolderRepository(grpc: grpc)

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
        let albumRef = self.albumRepository
        uploads.configure(transfer: transferRef) { snapshot, retry in
            do {
                switch snapshot.intent {
                case .none:
                    break
                case .attachDirectory:
                    guard let directoryID = snapshot.directoryID, !directoryID.isEmpty else { break }
                    try await cloudRef.attachFile(
                        fileID: snapshot.fileID,
                        directoryID: directoryID,
                        name: snapshot.fileName,
                        uploadSessionID: snapshot.sessionID,
                        isUploadRetry: retry
                    )
                case .routeByMediaKind:
                    try await cloudRef.attachFile(
                        fileID: snapshot.fileID,
                        directoryID: "",
                        name: snapshot.fileName,
                        routeByMediaKind: true,
                        uploadSessionID: snapshot.sessionID,
                        isUploadRetry: retry
                    )
                case .addToAlbum:
                    guard let albumID = snapshot.albumID, !albumID.isEmpty else { break }
                    try await albumRef.addItems(albumID: albumID, fileIDs: [snapshot.fileID])
                }
                if let localIdentifier = snapshot.localIdentifier, !localIdentifier.isEmpty {
                    await CloudDeviceLinkStore.shared.link(
                        fileID: snapshot.fileID,
                        localIdentifier: localIdentifier
                    )
                }
                return .completed
            } catch let error as RPCError where error.errorCode == DomainErrorCodes.fileAlreadyAttached {
                return .completed
            } catch {
                return .failed(domainErrorMessage(error))
            }
        }
        uploads.addObserver(failure: { snapshot in
            guard snapshot.state == .failed,
                  snapshot.retryable,
                  snapshot.retries < UploadConstants.maxUploadRetries else { return }
            scheduleRetryBGTaskIfNeeded()
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
        // Сначала миграция отменяет V1 queue и удаляет только её артефакты.
        // Затем возобновляем V2 jobs, backup и Share Inbox.
        Task {
            await uploads.migrateLegacyQueueIfNeeded()
            await uploads.attachAndResubmitOrphans()
            await MainActor.run {
                backupManager.resumeIfEnabled()
                shareInboxUploader.uploadPendingIfNeeded()
            }
        }
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
        ShareInbox.purgeAll()
        backupManager.setAutoUpload(false)
        RemoteImageCache.shared.clear()
        InsecureHTTP.clearCaches()
        await fileCache.clearAll()
        await AssetHashStore.shared.clearAll()
        await CloudDeviceLinkStore.shared.clearAll()
        fileCacheSettings.reset()
        appLockSettings.disable()
        vault.removeAll()
        RecentMediaWidgetBridge.clear()
        language.reset()
        serverConfig.reset()
    }
}
