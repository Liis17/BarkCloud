import Foundation

/// Обёртка над `UserDefaults` для настроек дискового кеша: лимит размера и время
/// последнего стартового sweep'а. Потокобезопасна (UserDefaults), поэтому шарится
/// между актором кеша и `@MainActor`-вьюмоделью настроек.
final class FileCacheSettings: @unchecked Sendable {
    /// Дефолтный лимит — 5 ГБ.
    static let defaultMaxBytes: Int64 = 5 * 1024 * 1024 * 1024
    /// Дефолтный порог автоочистки по возрасту — 7 дней.
    static let defaultStaleMaxAge: TimeInterval = 7 * 24 * 3600

    private let defaults: UserDefaults
    private let maxBytesKey = "BarkCloudCache.maxCacheBytes"
    private let lastSweepKey = "BarkCloudCache.lastSweepAt"
    private let staleMaxAgeKey = "BarkCloudCache.staleMaxAge"

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

    /// Порог автоочистки по возрасту: записи, к которым не обращались дольше этого
    /// времени, удаляются стартовым sweep'ом. `nil` — автоочистка по возрасту
    /// отключена (на диске хранится `0`); лимит по размеру при этом всё равно работает.
    var staleMaxAge: TimeInterval? {
        get {
            guard defaults.object(forKey: staleMaxAgeKey) != nil else { return Self.defaultStaleMaxAge }
            let value = defaults.double(forKey: staleMaxAgeKey)
            return value > 0 ? value : nil
        }
        set { defaults.set(newValue ?? 0, forKey: staleMaxAgeKey) }
    }

    var lastSweepAt: Date? {
        get { defaults.object(forKey: lastSweepKey) as? Date }
        set { defaults.set(newValue, forKey: lastSweepKey) }
    }

    /// Сброс к дефолтам — при полном сбросе устройства.
    func reset() {
        defaults.removeObject(forKey: maxBytesKey)
        defaults.removeObject(forKey: staleMaxAgeKey)
        defaults.removeObject(forKey: lastSweepKey)
    }
}
