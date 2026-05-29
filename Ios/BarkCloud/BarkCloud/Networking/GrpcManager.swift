import Foundation
import GRPCCore
import GRPCNIOTransportHTTP2
import SwiftProtobuf

/// Эндпоинты микросервисов. nginx терминирует TLS и маршрутизирует gRPC по портам
/// (см. Backend/nginx/cloud.barkfluff.conf): Identity :7020, Users :7021, Files :7025.
enum GrpcEndpoint {
    static let host = "cloud.barkfluff.com"
    static let identityPort = 7020
    static let usersPort = 7021
    static let filesPort = 7025
    static let useTLS = true
    static let allowSelfSigned = true

    /// База HTTP-раздачи файлов через nginx (`/web/download/{id}`, `/web/upload/{id}`).
    static var filesWebBase: String { "https://\(host):\(filesPort)/web" }

    /// Адрес веб-UI (порт 443) — туда ведут публичные share-ссылки `/s/{token}`.
    /// Маршрут `/s/...` обслуживает только веб-сервер, не gRPC Files.
    static var webHost: String { "https://\(host)" }

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
    private var clients: [Int: GRPCClient<Transport>] = [:]
    private var runTasks: [Int: Task<Void, Error>] = [:]

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
        IdentityClient(wrapping: try await client(port: GrpcEndpoint.identityPort))
    }

    func usersStub() async throws -> UsersClient {
        UsersClient(wrapping: try await client(port: GrpcEndpoint.usersPort))
    }

    func filesStub() async throws -> FilesClient {
        FilesClient(wrapping: try await client(port: GrpcEndpoint.filesPort))
    }

    func cloudStub() async throws -> CloudClient {
        CloudClient(wrapping: try await client(port: GrpcEndpoint.filesPort))
    }

    func albumStub() async throws -> AlbumClient {
        AlbumClient(wrapping: try await client(port: GrpcEndpoint.filesPort))
    }

    private func client(port: Int) async throws -> GRPCClient<Transport> {
        if let existing = clients[port] { return existing }

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
            target: .dns(host: GrpcEndpoint.host, port: port),
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
        clients[port] = client
        runTasks[port] = Task { [client] in
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

    /// Токен для запроса конкретного метода. `CreateToken` — публичный RPC
    /// обновления: не прикрепляем токен и не запускаем refresh (иначе рекурсия
    /// refresh → CreateToken → refresh). Прочие методы получают валидный токен.
    func accessToken(forMethod method: String) async -> String? {
        if method == "CreateToken" { return nil }
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
