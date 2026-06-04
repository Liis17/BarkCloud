import Foundation
import Security
import Observation

@MainActor
@Observable
public final class SessionStore {
    private let service = "com.barkfluff.BarkCloud.tokens"

    public init() {}

    /// Снимок всех токенов и их сроков — читается актором `GrpcManager` за один
    /// hop на главный поток при проактивной проверке/обновлении токена.
    struct Snapshot: Sendable {
        var accessToken: String?
        var accessTokenExpiresAt: Date?
        var refreshToken: String?
        var refreshTokenExpiresAt: Date?
    }

    /// Поднимается, когда обновить access-токен окончательно не удалось
    /// (refresh-токен истёк или отозван сервером). `RootView` реагирует переходом
    /// на экран логина. Сбрасывается при новой авторизации.
    public var sessionExpired = false

    private enum Key: String {
        case accessToken = "access_token"
        case accessTokenExpiresAt = "access_token_exp"
        case refreshToken = "refresh_token"
        case refreshTokenExpiresAt = "refresh_token_exp"
    }

    public var accessToken: String? {
        get { read(.accessToken) }
        set { write(.accessToken, newValue) }
    }

    public var accessTokenExpiresAt: Date? {
        get { readDate(.accessTokenExpiresAt) }
        set { writeDate(.accessTokenExpiresAt, newValue) }
    }

    public var refreshToken: String? {
        get { read(.refreshToken) }
        set { write(.refreshToken, newValue) }
    }

    public var refreshTokenExpiresAt: Date? {
        get { readDate(.refreshTokenExpiresAt) }
        set { writeDate(.refreshTokenExpiresAt, newValue) }
    }

    public func hasValidRefreshToken() -> Bool {
        guard let token = refreshToken, !token.isEmpty else { return false }
        guard let exp = refreshTokenExpiresAt else { return true }
        return exp > Date()
    }

    /// Атомарный снимок токенов (один проход по Keychain).
    func snapshot() -> Snapshot {
        Snapshot(
            accessToken: accessToken,
            accessTokenExpiresAt: accessTokenExpiresAt,
            refreshToken: refreshToken,
            refreshTokenExpiresAt: refreshTokenExpiresAt
        )
    }

    /// Сохранить обновлённый access-токен (refresh-токен не трогаем).
    public func saveRefreshedAccessToken(_ value: String, expiresAt: Date?) {
        accessToken = value
        accessTokenExpiresAt = expiresAt
    }

    /// Сессия мертва: чистим токены и поднимаем флаг для перехода на логин.
    public func invalidate() {
        clearSession()
        sessionExpired = true
    }

    public func clearSession() {
        for key in [Key.accessToken, .accessTokenExpiresAt, .refreshToken, .refreshTokenExpiresAt] {
            delete(key)
        }
    }

    private func baseQuery(_ key: Key) -> [String: Any] {
        // `kSecAttrAccessGroup` намеренно не указываем: Keychain при наличии
        // `keychain-access-groups` entitlement автоматически кладёт запись в
        // первую группу из массива (общую с Share Extension) и при чтении ищет
        // во всех группах, доступных приложению. Это работает идентично на
        // симуляторе и устройстве и не требует подстановки `$(AppIdentifierPrefix)`,
        // который в Swift не раскрывается.
        return [
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
