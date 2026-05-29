import Foundation

/// Обёртка над `UserDefaults` для настроек резервного копирования. Пока хранит один
/// флаг — включена ли автозагрузка фото/видео в облако. Потокобезопасна
/// (UserDefaults), поэтому шарится между `@MainActor`-менеджером и вью.
final class AutoUploadSettings: @unchecked Sendable {
    private let defaults: UserDefaults
    private let enabledKey = "BarkCloud.autoUpload.enabled"

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    var autoUploadEnabled: Bool {
        get { defaults.bool(forKey: enabledKey) }
        set { defaults.set(newValue, forKey: enabledKey) }
    }
}
