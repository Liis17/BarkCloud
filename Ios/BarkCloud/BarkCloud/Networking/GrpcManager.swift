import Foundation
import GRPCCore
import GRPCNIOTransportHTTP2

/// Эндпоинты микросервисов. nginx терминирует TLS и маршрутизирует gRPC по портам
/// (см. Backend/nginx/cloud.barkfluff.conf): Identity :7020, Users :7021, Files :7025.
enum GrpcEndpoint {
    static let host = "cloud.barkfluff.com"
    static let identityPort = 7020
    static let usersPort = 7021
    static let filesPort = 7025
    static let useTLS = true
    static let allowSelfSigned = true
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

    private let tokenProvider: @Sendable () async -> String?
    private var clients: [Int: GRPCClient<Transport>] = [:]
    private var runTasks: [Int: Task<Void, Error>] = [:]

    init(tokenProvider: @escaping @Sendable () async -> String?) {
        self.tokenProvider = tokenProvider
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
            AuthInterceptor(tokenProvider: tokenProvider),
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
    }
}
