import Foundation
import WidgetKit

/// Канал передачи квоты облака из main app в виджет хранилища. Виджет живёт в
/// отдельном процессе и не может ходить в gRPC — поэтому при каждом обновлении
/// квоты приложение кладёт три примитива в App Group `UserDefaults`, а виджет
/// читает их в своём `TimelineProvider`. Ключи продублированы строками в
/// `StorageSnapshot` виджета — это единственный контракт между таргетами.
enum StorageWidgetBridge {
    private static let usedKey = "storage_widget.used"
    private static let limitKey = "storage_widget.limit"
    private static let updatedAtKey = "storage_widget.updatedAt"

    private static var defaults: UserDefaults? {
        UserDefaults(suiteName: UploadConstants.appGroupID)
    }

    /// Записать снимок квоты и попросить систему перерисовать виджеты. Лимит ≤ 0
    /// означает «квота неизвестна» — виджет покажет заглушку.
    static func update(used: Int64, limit: Int64) {
        guard let defaults else { return }
        defaults.set(used, forKey: usedKey)
        defaults.set(limit, forKey: limitKey)
        defaults.set(Date().timeIntervalSince1970, forKey: updatedAtKey)
        WidgetCenter.shared.reloadTimelines(ofKind: "StorageWidget")
    }
}
