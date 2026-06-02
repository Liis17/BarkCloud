import Foundation
import Observation
import SwiftUI

/// Источник истины для языка интерфейса. Хранит выбор в [[LanguageSettings]],
/// применяет его к `Bundle.main` (через `Bundle.setAppLanguage`) для программных
/// строк и отдаёт `locale` для `.environment(\.locale,…)` на корне приложения.
///
/// Смена языка — мгновенная: `@Observable selected` перерисовывает UI, а корневой
/// `.environment(\.locale,…)` перелокализует все `Text`/форматтеры на месте.
@MainActor
@Observable
final class LanguageManager {
    private let settings: LanguageSettings

    private(set) var selected: AppLanguage

    init(settings: LanguageSettings) {
        self.settings = settings
        self.selected = settings.selected
        Bundle.setAppLanguage(selected.localeIdentifier)
    }

    /// Локаль для environment: системная (autoupdating) или явно выбранная.
    var locale: Locale {
        guard let id = selected.localeIdentifier else { return .autoupdatingCurrent }
        return Locale(identifier: id)
    }

    func setLanguage(_ lang: AppLanguage) {
        guard lang != selected else { return }
        settings.selected = lang
        Bundle.setAppLanguage(lang.localeIdentifier)
        selected = lang
    }

    /// Сброс к системному языку (полная очистка при выходе/удалении аккаунта).
    func reset() {
        setLanguage(.system)
    }
}
