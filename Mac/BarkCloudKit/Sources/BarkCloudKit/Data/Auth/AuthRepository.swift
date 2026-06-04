import Foundation
import GRPCCore
import SwiftProtobuf

public final class AuthRepository: Sendable {
    private let grpc: GrpcManager
    private let session: SessionStore

    public init(grpc: GrpcManager, session: SessionStore) {
        self.grpc = grpc
        self.session = session
    }

    public func auth(login: String, password: String, otpCode: String? = nil) async -> AuthResult {
        do {
            let stub = try await grpc.identityStub()
            var req = Barkcloud_Identity_AuthRequest()
            req.password = password
            if login.contains("@") {
                req.email = login
            } else {
                req.username = login
            }
            if let otp = otpCode, !otp.isEmpty {
                req.otpCode = otp
            }
            let response = try await stub.auth(req)
            await persist(response)
            return .success
        } catch let err as RPCError {
            switch err.errorCode {
            case AuthErrorCodes.otpRequired:
                return .otpRequired
            case AuthErrorCodes.invalidCredentials:
                return .invalidCredentials
            default:
                return .otherError(err.message)
            }
        } catch {
            return .otherError(error.localizedDescription)
        }
    }

    /// Серверный отзыв текущей сессии. Использует auth-контекст из интерсепторов
    /// (`x-auth-token` + устройство), поэтому вызывается до очистки токенов.
    /// Best-effort: ошибки игнорируются — сессия могла истечь или отсутствует сеть.
    public func logout() async {
        do {
            let stub = try await grpc.identityStub()
            _ = try await stub.logout(Barkcloud_Identity_LogoutRequest())
        } catch {
            // best-effort: локальная очистка всё равно выполнится
        }
    }

    @MainActor
    private func persist(_ response: Barkcloud_Identity_AuthResponse) {
        let access = response.accessToken
        let refresh = response.refreshToken
        session.accessToken = access.value
        session.accessTokenExpiresAt = access.hasExpirationDate ? Self.date(from: access.expirationDate) : nil
        session.refreshToken = refresh.value
        session.refreshTokenExpiresAt = refresh.hasExpirationDate ? Self.date(from: refresh.expirationDate) : nil
        session.sessionExpired = false
    }

    private static func date(from ts: SwiftProtobuf.Google_Protobuf_Timestamp) -> Date {
        Date(timeIntervalSince1970: TimeInterval(ts.seconds) + TimeInterval(ts.nanos) / 1_000_000_000)
    }
}
