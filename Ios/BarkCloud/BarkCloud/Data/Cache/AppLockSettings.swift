import Foundation
import Observation
import Security
import CommonCrypto

/// Локальная блокировка приложения: Face ID на входе + резервный PIN-код.
///
/// Что и где хранится:
/// - `isEnabled` и `failedAttempts` — в `UserDefaults` (переживают перезапуск, не
///   секретны).
/// - `pinSalt` и `pinHash` — в Keychain
///   (`kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly`), привязаны к устройству.
///
/// Хеширование: PBKDF2-HMAC-SHA256, 100 000 итераций, 16-байтовая соль, 32-байтовый
/// derived key. Соль и хеш живут раздельными ключами в одном `service`.
@MainActor
@Observable
final class AppLockSettings {
    static let maxFailedAttempts = 3
    private static let pinIterations: UInt32 = 100_000
    private static let saltLength = 16
    private static let derivedKeyLength = 32

    private let defaults: UserDefaults
    private let service = "com.barkfluff.BarkCloud.appLock"

    private enum DefaultsKey {
        static let isEnabled = "BarkCloud.appLock.isEnabled"
        static let failedAttempts = "BarkCloud.appLock.failedAttempts"
    }

    private enum KeychainKey: String {
        case salt = "pin_salt"
        case hash = "pin_hash"
    }

    private(set) var isEnabled: Bool
    private(set) var failedAttempts: Int

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        self.isEnabled = defaults.bool(forKey: DefaultsKey.isEnabled)
        self.failedAttempts = defaults.integer(forKey: DefaultsKey.failedAttempts)
    }

    var remainingAttempts: Int { max(0, Self.maxFailedAttempts - failedAttempts) }

    /// Включить блокировку с заданным PIN. Соль и хеш сохраняются в Keychain.
    func enable(pin: String) {
        let salt = Self.randomSalt()
        let hash = Self.derive(pin: pin, salt: salt)
        writeKeychain(.salt, salt)
        writeKeychain(.hash, hash)
        defaults.set(true, forKey: DefaultsKey.isEnabled)
        defaults.set(0, forKey: DefaultsKey.failedAttempts)
        isEnabled = true
        failedAttempts = 0
    }

    /// Выключить блокировку и стереть всё связанное.
    func disable() {
        deleteKeychain(.salt)
        deleteKeychain(.hash)
        defaults.removeObject(forKey: DefaultsKey.isEnabled)
        defaults.removeObject(forKey: DefaultsKey.failedAttempts)
        isEnabled = false
        failedAttempts = 0
    }

    /// Сравнить введённый PIN с сохранённым (constant-time).
    func verify(pin: String) -> Bool {
        guard let salt = readKeychain(.salt), let stored = readKeychain(.hash) else { return false }
        let candidate = Self.derive(pin: pin, salt: salt)
        return Self.constantTimeEqual(candidate, stored)
    }

    /// Зарегистрировать неудачную попытку. Возвращает `true`, если лимит исчерпан.
    func registerFailure() -> Bool {
        failedAttempts += 1
        defaults.set(failedAttempts, forKey: DefaultsKey.failedAttempts)
        return failedAttempts >= Self.maxFailedAttempts
    }

    func resetFailures() {
        failedAttempts = 0
        defaults.set(0, forKey: DefaultsKey.failedAttempts)
    }

    // MARK: - PBKDF2

    private static func derive(pin: String, salt: Data) -> Data {
        var derived = Data(count: derivedKeyLength)
        let pinBytes = Array(pin.utf8)
        let status = derived.withUnsafeMutableBytes { derivedPtr -> Int32 in
            salt.withUnsafeBytes { saltPtr -> Int32 in
                CCKeyDerivationPBKDF(
                    CCPBKDFAlgorithm(kCCPBKDF2),
                    pinBytes, pinBytes.count,
                    saltPtr.bindMemory(to: UInt8.self).baseAddress, salt.count,
                    CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256),
                    pinIterations,
                    derivedPtr.bindMemory(to: UInt8.self).baseAddress, derivedKeyLength
                )
            }
        }
        precondition(status == kCCSuccess, "PBKDF2 derivation failed: \(status)")
        return derived
    }

    private static func randomSalt() -> Data {
        var bytes = [UInt8](repeating: 0, count: saltLength)
        let status = SecRandomCopyBytes(kSecRandomDefault, saltLength, &bytes)
        precondition(status == errSecSuccess, "SecRandomCopyBytes failed: \(status)")
        return Data(bytes)
    }

    private static func constantTimeEqual(_ a: Data, _ b: Data) -> Bool {
        guard a.count == b.count else { return false }
        var diff: UInt8 = 0
        for i in 0..<a.count { diff |= a[i] ^ b[i] }
        return diff == 0
    }

    // MARK: - Keychain

    private func baseQuery(_ key: KeychainKey) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: key.rawValue
        ]
    }

    private func readKeychain(_ key: KeychainKey) -> Data? {
        var query = baseQuery(key)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        guard status == errSecSuccess else { return nil }
        return result as? Data
    }

    private func writeKeychain(_ key: KeychainKey, _ value: Data) {
        let query = baseQuery(key)
        let attrs: [String: Any] = [
            kSecValueData as String: value,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        ]
        let status = SecItemUpdate(query as CFDictionary, attrs as CFDictionary)
        if status == errSecItemNotFound {
            var insert = query
            insert[kSecValueData as String] = value
            insert[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
            SecItemAdd(insert as CFDictionary, nil)
        }
    }

    private func deleteKeychain(_ key: KeychainKey) {
        SecItemDelete(baseQuery(key) as CFDictionary)
    }
}
