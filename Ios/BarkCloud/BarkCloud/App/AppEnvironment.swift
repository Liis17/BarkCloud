import Foundation
import Observation
import SwiftData

@MainActor
@Observable
final class AppEnvironment {
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
    let vault: VaultStore
    let biometric: BiometricGate
    let shareInboxUploader: ShareInboxUploader

    init() {
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
        self.shareInboxUploader = ShareInboxUploader(cloud: self.cloudRepository, session: session)

        Task { await cache.runStartupSweepIfNeeded() }
        // Догрузить то, что Share Extension сложил в общий контейнер.
        shareInboxUploader.uploadPendingIfNeeded()
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

    /// Очистить все локальные данные приложения: токены в Keychain, кэшированные
    /// gRPC-соединения, кэш изображений и URL-кэш. Используется при выходе и при
    /// удалении аккаунта (когда серверный отзыв сессии не нужен).
    func resetLocalState() async {
        sessionStore.clearSession()
        await grpcManager.shutdown()
        RemoteImageCache.shared.clear()
        InsecureHTTP.clearCaches()
        await fileCache.clearAll()
        await AssetHashStore.shared.clearAll()
    }
}
