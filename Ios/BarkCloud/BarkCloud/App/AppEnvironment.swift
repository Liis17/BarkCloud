import Foundation
import Observation

@MainActor
@Observable
final class AppEnvironment {
    let sessionStore: SessionStore
    let grpcManager: GrpcManager
    let authRepository: AuthRepository
    let localFileRepository: LocalFileRepository

    init() {
        let session = SessionStore()
        let grpc = GrpcManager { [weak session] in
            await MainActor.run { session?.accessToken }
        }
        self.sessionStore = session
        self.grpcManager = grpc
        self.authRepository = AuthRepository(grpc: grpc, session: session)
        self.localFileRepository = LocalFileRepository()
    }
}
