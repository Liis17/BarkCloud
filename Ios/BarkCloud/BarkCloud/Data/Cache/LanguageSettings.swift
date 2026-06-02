import Foundation
import SwiftUI

/// Язык интерфейса, выбранный пользователем. `.system` — следовать языку
/// устройства (поведение iOS по умолчанию). Остальные кейсы — явный выбор.
enum AppLanguage: String, CaseIterable, Identifiable {
    case system
    case ru
    case en
    case de

    var id: String { rawValue }

    /// `nil` для системного режима, иначе код локали для `.lproj`/`Locale`.
    var localeIdentifier: String? {
        self == .system ? nil : rawValue
    }

    /// Название пункта в списке. Языки показываем эндонимами (одинаково во всех
    /// локалях), «Системный» — переводимый ключ.
    var displayNameKey: LocalizedStringResource {
        switch self {
        case .system: return "language_system"
        case .ru: return "language_ru"
        case .en: return "language_en"
        case .de: return "language_de"
        }
    }

    /// Эмодзи-флаг для строки в настройках. У «Системного» нет страны — глобус.
    var flag: String {
        switch self {
        case .system: return "🌐"
        case .ru: return "🇷🇺"
        case .en: return "🇬🇧"
        case .de: return "🇩🇪"
        }
    }
}

/// Обёртка над `UserDefaults` для выбранного языка интерфейса. Потокобезопасна
/// (UserDefaults), поэтому шарится между `@MainActor`-менеджером и вью.
final class LanguageSettings: @unchecked Sendable {
    private let defaults: UserDefaults
    private let languageKey = "BarkCloud.app.language"

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    var selected: AppLanguage {
        get {
            guard let raw = defaults.string(forKey: languageKey),
                  let lang = AppLanguage(rawValue: raw) else { return .system }
            return lang
        }
        set { defaults.set(newValue.rawValue, forKey: languageKey) }
    }
}
