import Foundation
import Observation
import BarkCloudKit

/// Сервис-контейнер и состояние контейнер-приложения macOS-клиента. Переиспользует
/// сетевой слой `BarkCloudKit` (тот же, что iOS и FSKit-расширение).
@MainActor
@Observable
final class AppModel {
    /// Экран по состоянию: ввод адреса сервера → логин → дашборд.
    enum Phase { case serverSetup, login, dashboard }

    let session: SessionStore
    let grpc: GrpcManager
    let transfer: FileTransferService
    let auth: AuthRepository
    let users: UserRepository
    let domain = FileProviderDomainManager()

    var phase: Phase
    var user: Barkcloud_Users_User?
    var storageUsed: Int64 = 0
    var storageLimit: Int64 = 0
    var isBusy = false
    var errorMessage: String?
    /// Сервер запросил OTP — показываем поле кода.
    var otpRequired = false

    init() {
        let s = SessionStore()
        self.session = s
        let g = GrpcManager(session: s)
        self.grpc = g
        let t = FileTransferService(grpc: g)
        self.transfer = t
        self.auth = AuthRepository(grpc: g, session: s)
        self.users = UserRepository(grpc: g, transfer: t)
        self.phase = AppModel.phase(for: s)
    }

    static func phase(for session: SessionStore) -> Phase {
        guard ServerConfig.isConfigured else { return .serverSetup }
        return session.hasValidRefreshToken() ? .dashboard : .login
    }

    func refreshPhase() { phase = AppModel.phase(for: session) }

    // MARK: - Server setup

    func saveServer(_ config: ServerConfig) {
        config.persist()
        otpRequired = false
        errorMessage = nil
        refreshPhase()
    }

    /// Забыть адрес сервера (и сессию) — возврат к экрану ввода адресов.
    func forgetServer() async {
        await clearSession()
        ServerConfig.clear()
        refreshPhase()
    }

    // MARK: - Login

    func login(login: String, password: String, otp: String?) async {
        guard !isBusy else { return }
        isBusy = true; defer { isBusy = false }
        errorMessage = nil
        let result = await auth.auth(login: login, password: password, otpCode: otp?.isEmpty == true ? nil : otp)
        switch result {
        case .success:
            otpRequired = false
            refreshPhase()
            await loadProfile()
        case .otpRequired:
            otpRequired = true
        case .invalidCredentials:
            errorMessage = String(localized: "Неверный логин или пароль")
        case .otherError(let message):
            errorMessage = message
        }
    }

    func logout() async {
        await clearSession()
        refreshPhase()
    }

    private func clearSession() async {
        await auth.logout()
        session.clearSession()
        InsecureHTTP.clearCaches()
        await grpc.shutdown()
        await domain.purge()
        AppModel.clearProviderCache()
        user = nil
        storageUsed = 0
        storageLimit = 0
    }

    /// Удалить persistent cache File Provider'a в App Group container.
    /// Расширение в новом запуске поднимется с пустым снимком.
    private static func clearProviderCache() {
        guard let container = BarkCloudAppGroup.containerURL else { return }
        let cacheFile = container
            .appendingPathComponent("FileProvider", isDirectory: true)
            .appendingPathComponent("items-cache.json")
        try? FileManager.default.removeItem(at: cacheFile)
    }

    // MARK: - Dashboard data

    func loadProfile() async {
        user = try? await users.getUser(userID: 0)
        if let info = try? await transfer.storageInfo() {
            storageUsed = info.used
            storageLimit = info.limit
            StorageWidgetBridge.update(used: info.used, limit: info.limit)
        }
    }
}
