import Foundation
import WidgetKit

/// Канал передачи состояния «сейфа» из main app в `VaultWidget`. Сейф —
/// приватный раздел за биометрией; число элементов на Lock Screen это метаданные,
/// поэтому показ счётчика **opt-in** (по умолчанию выключен): при выключенном
/// показе виджет рисует только замок без числа. Флаг и счётчик лежат в App Group
/// `UserDefaults`; ключи продублированы строками в `VaultSnapshot` виджета.
enum VaultWidgetBridge {
    private static let countKey = "vault_widget.count"
    private static let enabledKey = "vault_widget.enabled"

    private static var defaults: UserDefaults? {
        UserDefaults(suiteName: UploadConstants.appGroupID)
    }

    /// Показывать ли число (по умолчанию — нет).
    static var isCountVisible: Bool { defaults?.bool(forKey: enabledKey) ?? false }

    /// Сохранить актуальный счётчик. Виджет покажет его только если показ включён.
    static func update(count: Int) {
        guard let defaults else { return }
        defaults.set(count, forKey: countKey)
        WidgetCenter.shared.reloadTimelines(ofKind: "VaultWidget")
    }

    /// Включить/выключить показ числа на виджете (privacy opt-in).
    static func setCountVisible(_ visible: Bool) {
        guard let defaults else { return }
        defaults.set(visible, forKey: enabledKey)
        WidgetCenter.shared.reloadTimelines(ofKind: "VaultWidget")
    }
}
