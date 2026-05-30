import Foundation
import GRPCCore
import GRPCNIOTransportHTTP2
import SwiftProtobuf

/// Конфигурация адресов сервера. BarkCloud — self-hosted, поэтому адреса
/// микросервисов задаёт пользователь при первом запуске (см. `ServerSetupScreen`)
/// и хранятся в App Group UserDefaults — общем хранилище main app и Share Extension
/// (тот же контейнер `group.com.barkfluff.BarkCloud`, что у фоновой загрузки).
struct ServerConfig: Sendable, Equatable {
    var identityHost: String
    var identityPort: Int
    var usersHost: String
    var usersPort: Int
    var filesHost: String
    var filesPort: Int
    var useTLS: Bool
    var allowSelfSigned: Bool

    /// Значения боевого деплоя — дефолт и предзаполнение формы первого запуска.
    static let production = ServerConfig(
        identityHost: "cloud.barkfluff.com", identityPort: 7020,
        usersHost: "cloud.barkfluff.com", usersPort: 7021,
        filesHost: "cloud.barkfluff.com", filesPort: 7025,
        useTLS: true, allowSelfSigned: true
    )

    private enum Key {
        static let identityHost = "BarkCloud.server.identityHost"
        static let identityPort = "BarkCloud.server.identityPort"
        static let usersHost = "BarkCloud.server.usersHost"
        static let usersPort = "BarkCloud.server.usersPort"
        static let filesHost = "BarkCloud.server.filesHost"
        static let filesPort = "BarkCloud.server.filesPort"
        static let useTLS = "BarkCloud.server.useTLS"
        static let allowSelfSigned = "BarkCloud.server.allowSelfSigned"
        static let configured = "BarkCloud.server.configured"
    }

    private static var store: UserDefaults {
        UserDefaults(suiteName: UploadConstants.appGroupID) ?? .standard
    }

    /// Пользователь хотя бы раз сохранил адреса. До этого `RootView` показывает экран ввода.
    static var isConfigured: Bool { store.bool(forKey: Key.configured) }

    /// Текущая конфигурация. До первой настройки — `production`-дефолты.
    static var current: ServerConfig {
        let d = store
        guard d.bool(forKey: Key.configured) else { return .production }
        let p = ServerConfig.production
        func port(_ key: String, _ fallback: Int) -> Int {
            let v = d.integer(forKey: key)
            return v > 0 ? v : fallback
        }
        return ServerConfig(
            identityHost: d.string(forKey: Key.identityHost) ?? p.identityHost,
            identityPort: port(Key.identityPort, p.identityPort),
            usersHost: d.string(forKey: Key.usersHost) ?? p.usersHost,
            usersPort: port(Key.usersPort, p.usersPort),
            filesHost: d.string(forKey: Key.filesHost) ?? p.filesHost,
            filesPort: port(Key.filesPort, p.filesPort),
            useTLS: d.object(forKey: Key.useTLS) as? Bool ?? p.useTLS,
            allowSelfSigned: d.object(forKey: Key.allowSelfSigned) as? Bool ?? p.allowSelfSigned
        )
    }

    /// Сохранить адреса и отметить конфигурацию завершённой.
    func persist() {
        let d = Self.store
        d.set(identityHost, forKey: Key.identityHost)
        d.set(identityPort, forKey: Key.identityPort)
        d.set(usersHost, forKey: Key.usersHost)
        d.set(usersPort, forKey: Key.usersPort)
        d.set(filesHost, forKey: Key.filesHost)
        d.set(filesPort, forKey: Key.filesPort)
        d.set(useTLS, forKey: Key.useTLS)
        d.set(allowSelfSigned, forKey: Key.allowSelfSigned)
        d.set(true, forKey: Key.configured)
    }

    /// Полный сброс адресов сервера — после него `isConfigured == false`, и
    /// `RootView` снова показывает экран ввода адресов (`ServerSetupScreen`).
    static func clear() {
        let d = store
        [Key.identityHost, Key.identityPort, Key.usersHost, Key.usersPort,
         Key.filesHost, Key.filesPort, Key.useTLS, Key.allowSelfSigned, Key.configured]
            .forEach(d.removeObject(forKey:))
    }
}

/// Эндпоинты микросервисов поверх `ServerConfig`. nginx терминирует TLS и
/// маршрутизирует gRPC по портам (см. Backend/nginx/cloud.barkfluff.conf):
/// Identity :7020, Users :7021, Files :7025 — но хосты/порты задаёт пользователь.
enum GrpcEndpoint {
    static var identityHost: String { ServerConfig.current.identityHost }
    static var identityPort: Int { ServerConfig.current.identityPort }
    static var usersHost: String { ServerConfig.current.usersHost }
    static var usersPort: Int { ServerConfig.current.usersPort }
    static var filesHost: String { ServerConfig.current.filesHost }
    static var filesPort: Int { ServerConfig.current.filesPort }
    static var useTLS: Bool { ServerConfig.current.useTLS }
    static var allowSelfSigned: Bool { ServerConfig.current.allowSelfSigned }

    private static var scheme: String { useTLS ? "https" : "http" }

    /// База HTTP-раздачи файлов через nginx (`/web/download/{id}`, `/web/upload/{id}`).
    static var filesWebBase: String { "\(scheme)://\(filesHost):\(filesPort)/web" }

    /// Адрес веб-UI (порт 443) — туда ведут публичные share-ссылки `/s/{token}`.
    /// Маршрут `/s/...` обслуживает только веб-сервер, не gRPC Files. Берём хост Files.
    static var webHost: String { "\(scheme)://\(filesHost)" }

    /// Публичный URL share-ссылки. `token` — base64url (URL-safe), экранировать не нужно.
    static func publicShareURL(token: String) -> URL? {
        guard !token.isEmpty else { return nil }
        return URL(string: "\(webHost)/s/\(token)")
    }

    /// Перестраивает ссылку скачивания файла на актуальный эндпоинт Files.
    /// Часть ссылок (например, URL аватара) хранится в БД и была сгенерирована
    /// при прежней конфигурации `ExternalEndpoint:Host` — она может указывать на
    /// недостижимый/устаревший хост. Берём идентификатор файла из пути
    /// `.../download/{id}` и собираем ссылку заново на текущем хосте. Если путь не
    /// похож на ссылку скачивания — возвращаем исходный URL без изменений.
    static func normalizedFileDownloadURL(_ raw: String) -> URL? {
        guard !raw.isEmpty else { return nil }
        guard let comps = URLComponents(string: raw) else { return URL(string: raw) }
        let parts = comps.path.split(separator: "/").map(String.init)
        if let idx = parts.lastIndex(of: "download"), idx + 1 < parts.count {
            return URL(string: "\(filesWebBase)/download/\(parts[idx + 1])")
        }
        return URL(string: raw)
    }
}

/// Управляет gRPC-клиентами ко всем сервисам. На каждый порт — один кэшированный
/// `GRPCClient` (общий транспорт + интерсепторы), поверх которого создаются
/// типизированные стабы. FilesApi / CloudApi / AlbumApi живут на одном порту (:7025)
/// и делят общий клиент.
actor GrpcManager {
    typealias Transport = HTTP2ClientTransport.Posix
    typealias IdentityClient = Barkcloud_Identity_IdentityApi.Client<Transport>
    typealias UsersClient = Barkcloud_Users_UsersApi.Client<Transport>
    typealias FilesClient = Barkcloud_Files_FilesApi.Client<Transport>
    typealias CloudClient = Barkcloud_Files_CloudApi.Client<Transport>
    typealias AlbumClient = Barkcloud_Files_AlbumApi.Client<Transport>

    private let session: SessionStore
    private var clients: [String: GRPCClient<Transport>] = [:]
    private var runTasks: [String: Task<Void, Error>] = [:]

    /// Текущая задача обновления access-токена. Гарантирует, что при множестве
    /// параллельных запросов обновление выполняется ровно один раз (остальные
    /// ждут результата). Актор-изоляция делает проверку-и-установку атомарной.
    private var refreshTask: Task<String, Error>?

    /// За сколько секунд до истечения токена обновлять его проактивно.
    private let proactiveRefreshThreshold: TimeInterval = 60

    init(session: SessionStore) {
        self.session = session
    }

    func identityStub() async throws -> IdentityClient {
        IdentityClient(wrapping: try await client(host: GrpcEndpoint.identityHost, port: GrpcEndpoint.identityPort))
    }

    func usersStub() async throws -> UsersClient {
        UsersClient(wrapping: try await client(host: GrpcEndpoint.usersHost, port: GrpcEndpoint.usersPort))
    }

    func filesStub() async throws -> FilesClient {
        FilesClient(wrapping: try await client(host: GrpcEndpoint.filesHost, port: GrpcEndpoint.filesPort))
    }

    func cloudStub() async throws -> CloudClient {
        CloudClient(wrapping: try await client(host: GrpcEndpoint.filesHost, port: GrpcEndpoint.filesPort))
    }

    func albumStub() async throws -> AlbumClient {
        AlbumClient(wrapping: try await client(host: GrpcEndpoint.filesHost, port: GrpcEndpoint.filesPort))
    }

    private func client(host: String, port: Int) async throws -> GRPCClient<Transport> {
        let key = "\(host):\(port)"
        if let existing = clients[key] { return existing }

        let transportSecurity: Transport.TransportSecurity
        if GrpcEndpoint.useTLS {
            transportSecurity = .tls { config in
                if GrpcEndpoint.allowSelfSigned {
                    config.serverCertificateVerification = .noVerification
                }
            }
        } else {
            transportSecurity = .plaintext
        }

        let transport = try Transport(
            target: .dns(host: host, port: port),
            transportSecurity: transportSecurity
        )

        let interceptors: [any ClientInterceptor] = [
            AuthInterceptor(accessTokenForMethod: { [weak self] method in
                await self?.accessToken(forMethod: method)
            }),
            XDeviceInterceptor(),
            XOsInterceptor(),
            XAppInterceptor(),
            XIpInterceptor()
        ]

        let client = GRPCClient(transport: transport, interceptors: interceptors)
        clients[key] = client
        runTasks[key] = Task { [client] in
            try await client.runConnections()
        }
        return client
    }

    func shutdown() async {
        for task in runTasks.values { task.cancel() }
        runTasks.removeAll()
        clients.removeAll()
        refreshTask = nil
    }

    // MARK: - Авто-обновление access-токена

    /// Публичные (без авторизации) RPC Identity, к которым НЕ прикрепляем
    /// `x-auth-token`: `Auth` (логин по паролю) и `CreateToken` (обновление по
    /// refresh). Если повесить на них просроченный/чужой токен из Keychain, его
    /// ловит auth-middleware сервера и отвечает HTTP 401 ещё до самого метода —
    /// логин падает с «non-200 HTTP Status Code (401)». `CreateToken` к тому же
    /// исключает рекурсию refresh → CreateToken → refresh.
    private static let unauthenticatedMethods: Set<String> = ["Auth", "CreateToken"]

    /// Токен для запроса конкретного метода. Публичным RPC отдаёт `nil`, прочие
    /// получают валидный (при необходимости проактивно обновлённый) токен.
    func accessToken(forMethod method: String) async -> String? {
        if Self.unauthenticatedMethods.contains(method) { return nil }
        return await validAccessToken()
    }

    /// Возвращает действующий access-токен, обновив его при необходимости.
    /// Используется интерсептором gRPC и HTTP-загрузкой файлов.
    func validAccessToken() async -> String? {
        let snap = await session.snapshot()

        // Токен ещё свеж — отдаём как есть.
        guard snap.accessToken == nil || isExpiringSoon(snap.accessTokenExpiresAt) else {
            return snap.accessToken
        }

        // Нечем обновлять — отдаём что есть (возможно, nil): запрос уйдёт без токена.
        guard let refresh = snap.refreshToken, !refresh.isEmpty else {
            return snap.accessToken
        }

        // Refresh-токен истёк локально — сессия мертва, обновление бессмысленно.
        if let refreshExp = snap.refreshTokenExpiresAt, refreshExp <= Date() {
            await session.invalidate()
            return nil
        }

        do {
            return try await refreshAccessToken(refreshToken: refresh)
        } catch {
            // Транзиентная ошибка (нет сети и т.п.) — пробуем со старым токеном.
            return snap.accessToken
        }
    }

    private func isExpiringSoon(_ expiry: Date?) -> Bool {
        guard let expiry else { return true }
        return expiry.timeIntervalSinceNow < proactiveRefreshThreshold
    }

    /// Сериализованное обновление: первый вызов запускает задачу, остальные ждут её.
    private func refreshAccessToken(refreshToken: String) async throws -> String {
        if let existing = refreshTask {
            return try await existing.value
        }

        let task = Task<String, Error> { [session] in
            let token = try await self.createTokenRaw(refreshToken: refreshToken)
            let expiry = token.hasExpirationDate ? Self.date(from: token.expirationDate) : nil
            await session.saveRefreshedAccessToken(token.value, expiresAt: expiry)
            return token.value
        }
        refreshTask = task

        do {
            let value = try await task.value
            refreshTask = nil
            return value
        } catch {
            refreshTask = nil
            // Сервер отверг refresh-токен → сессия истекла, уводим на логин.
            if let rpc = error as? RPCError, rpc.code == .unauthenticated {
                await session.invalidate()
            }
            throw error
        }
    }

    /// Вызов `Identity.CreateToken` напрямую. Идёт через обычный identity-клиент,
    /// но его AuthInterceptor для метода `CreateToken` не запускает обновление.
    private func createTokenRaw(refreshToken: String) async throws -> Barkcloud_Identity_Token {
        let stub = try await identityStub()
        var req = Barkcloud_Identity_CreateTokenRequest()
        req.refreshToken = refreshToken
        let resp = try await stub.createToken(req)
        return resp.accessToken
    }

    private static func date(from ts: SwiftProtobuf.Google_Protobuf_Timestamp) -> Date {
        Date(timeIntervalSince1970: TimeInterval(ts.seconds) + TimeInterval(ts.nanos) / 1_000_000_000)
    }
}
