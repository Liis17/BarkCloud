import Foundation
import GRPCCore
import GRPCNIOTransportHTTP2

enum GrpcEndpoint {
    static let identityHost = "localhost"
    static let identityPort = 5001
    static let useTLS = true
    static let allowSelfSigned = true
}

actor GrpcManager {
    typealias IdentityClient = Barkcloud_Identity_IdentityApi.Client<HTTP2ClientTransport.Posix>

    private let tokenProvider: @Sendable () async -> String?
    private var identity: (client: GRPCCore.GRPCClient<HTTP2ClientTransport.Posix>, stub: IdentityClient)?
    private var runTask: Task<Void, Error>?

    init(tokenProvider: @escaping @Sendable () async -> String?) {
        self.tokenProvider = tokenProvider
    }

    func identityStub() async throws -> IdentityClient {
        if let existing = identity { return existing.stub }

        let transportSecurity: HTTP2ClientTransport.Posix.TransportSecurity
        if GrpcEndpoint.useTLS {
            transportSecurity = .tls { config in
                if GrpcEndpoint.allowSelfSigned {
                    config.serverCertificateVerification = .noVerification
                }
            }
        } else {
            transportSecurity = .plaintext
        }

        let transport = try HTTP2ClientTransport.Posix(
            target: .dns(host: GrpcEndpoint.identityHost, port: GrpcEndpoint.identityPort),
            transportSecurity: transportSecurity
        )

        let interceptors: [any ClientInterceptor] = [
            AuthInterceptor(tokenProvider: tokenProvider),
            XDeviceInterceptor(),
            XOsInterceptor(),
            XAppInterceptor(),
            XIpInterceptor()
        ]

        let client = GRPCCore.GRPCClient(transport: transport, interceptors: interceptors)
        let stub = Barkcloud_Identity_IdentityApi.Client(wrapping: client)
        identity = (client, stub)

        runTask = Task { [client] in
            try await client.runConnections()
        }

        return stub
    }

    func shutdown() async {
        if let runTask { runTask.cancel() }
        identity = nil
        runTask = nil
    }
}
