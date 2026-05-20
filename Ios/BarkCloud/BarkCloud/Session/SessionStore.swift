import Foundation
import Security
import Observation

@MainActor
@Observable
final class SessionStore {
    private let service = "com.barkfluff.BarkCloud.tokens"

    private enum Key: String {
        case accessToken = "access_token"
        case accessTokenExpiresAt = "access_token_exp"
        case refreshToken = "refresh_token"
        case refreshTokenExpiresAt = "refresh_token_exp"
    }

    var accessToken: String? {
        get { read(.accessToken) }
        set { write(.accessToken, newValue) }
    }

    var accessTokenExpiresAt: Date? {
        get { readDate(.accessTokenExpiresAt) }
        set { writeDate(.accessTokenExpiresAt, newValue) }
    }

    var refreshToken: String? {
        get { read(.refreshToken) }
        set { write(.refreshToken, newValue) }
    }

    var refreshTokenExpiresAt: Date? {
        get { readDate(.refreshTokenExpiresAt) }
        set { writeDate(.refreshTokenExpiresAt, newValue) }
    }

    func hasValidRefreshToken() -> Bool {
        guard let token = refreshToken, !token.isEmpty else { return false }
        guard let exp = refreshTokenExpiresAt else { return true }
        return exp > Date()
    }

    func clearSession() {
        for key in [Key.accessToken, .accessTokenExpiresAt, .refreshToken, .refreshTokenExpiresAt] {
            delete(key)
        }
    }

    private func baseQuery(_ key: Key) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: key.rawValue
        ]
    }

    private func read(_ key: Key) -> String? {
        var query = baseQuery(key)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        guard status == errSecSuccess, let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    private func write(_ key: Key, _ value: String?) {
        guard let value, !value.isEmpty else { delete(key); return }
        let data = Data(value.utf8)
        let query = baseQuery(key)
        let attrs: [String: Any] = [
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        ]
        let status = SecItemUpdate(query as CFDictionary, attrs as CFDictionary)
        if status == errSecItemNotFound {
            var insert = query
            insert[kSecValueData as String] = data
            insert[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
            SecItemAdd(insert as CFDictionary, nil)
        }
    }

    private func delete(_ key: Key) {
        SecItemDelete(baseQuery(key) as CFDictionary)
    }

    private func readDate(_ key: Key) -> Date? {
        guard let raw = read(key), let ts = TimeInterval(raw) else { return nil }
        return Date(timeIntervalSince1970: ts)
    }

    private func writeDate(_ key: Key, _ value: Date?) {
        guard let value else { delete(key); return }
        write(key, String(value.timeIntervalSince1970))
    }
}
