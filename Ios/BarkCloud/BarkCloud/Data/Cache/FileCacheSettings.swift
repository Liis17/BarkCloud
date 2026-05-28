import Foundation

/// Обёртка над `UserDefaults` для настроек дискового кеша: лимит размера и время
/// последнего стартового sweep'а. Потокобезопасна (UserDefaults), поэтому шарится
/// между актором кеша и `@MainActor`-вьюмоделью настроек.
final class FileCacheSettings: @unchecked Sendable {
    /// Дефолтный лимит — 5 ГБ.
    static let defaultMaxBytes: Int64 = 5 * 1024 * 1024 * 1024

    private let defaults: UserDefaults
    private let maxBytesKey = "BarkCloudCache.maxCacheBytes"
    private let lastSweepKey = "BarkCloudCache.lastSweepAt"

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    var maxCacheBytes: Int64 {
        get {
            guard defaults.object(forKey: maxBytesKey) != nil else { return Self.defaultMaxBytes }
            return Int64(defaults.integer(forKey: maxBytesKey))
        }
        set { defaults.set(Int(newValue), forKey: maxBytesKey) }
    }

    var lastSweepAt: Date? {
        get { defaults.object(forKey: lastSweepKey) as? Date }
        set { defaults.set(newValue, forKey: lastSweepKey) }
    }
}
