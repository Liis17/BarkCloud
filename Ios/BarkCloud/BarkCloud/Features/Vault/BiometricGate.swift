import Foundation
import LocalAuthentication

/// Тонкая обёртка над `LocalAuthentication`. Используется для разблокировки
/// локального сейфа. Политика `deviceOwnerAuthentication` = биометрия (Face ID /
/// Touch ID) с автоматическим откатом на код-пароль устройства, поэтому сейф
/// доступен и на устройствах без биометрии.
@MainActor
final class BiometricGate {
    enum Availability {
        case faceID
        case touchID
        case passcodeOnly
        case unavailable
    }

    func availability() -> Availability {
        let ctx = LAContext()
        var error: NSError?
        if ctx.canEvaluatePolicy(.deviceOwnerAuthenticationWithBiometrics, error: &error) {
            switch ctx.biometryType {
            case .faceID: return .faceID
            case .touchID: return .touchID
            default: return .passcodeOnly
            }
        }
        if ctx.canEvaluatePolicy(.deviceOwnerAuthentication, error: &error) {
            return .passcodeOnly
        }
        return .unavailable
    }

    /// Запросить аутентификацию. Возвращает `true` только при успехе; любая
    /// ошибка/отмена → `false` (сейф остаётся заперт).
    func authenticate(reason: String) async -> Bool {
        let ctx = LAContext()
        do {
            return try await ctx.evaluatePolicy(.deviceOwnerAuthentication, localizedReason: reason)
        } catch {
            return false
        }
    }
}
