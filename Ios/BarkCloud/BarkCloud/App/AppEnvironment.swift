import Foundation
import Observation

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
    }
}
