import Foundation

public enum AuthResult: Sendable, Equatable {
    case success
    case otpRequired
    case invalidCredentials
    case otherError(String)
}
