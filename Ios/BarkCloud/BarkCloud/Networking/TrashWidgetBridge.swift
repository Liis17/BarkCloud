import Foundation
import WidgetKit

/// Канал передачи состояния корзины из main app в `TrashWidget`. Виджет живёт в
/// отдельном процессе и в gRPC не ходит — приложение кладёт счётчик в App Group
/// `UserDefaults`, а виджет читает его в `TimelineProvider`. Ключи продублированы
/// строками в `TrashSnapshot` виджета — это единственный контракт между таргетами.
///
/// Передаём количество (`hasMore` — был ли усечён первый лист в 50 записей) и
/// ближайшую дату авто-удаления. Список отдаётся в порядке `DeletedAt desc`, поэтому
/// ближайший к удалению (самый старый) элемент лежит на последней странице: точный
/// `min(purgeAt)` известен, только когда корзина помещается в один лист (`hasMore == false`).
/// Тогда передаём его; иначе `nil`, и виджет показывает статичную подсказку про
/// фиксированный 14-дневный срок хранения (`TrashPurgeService.Retention`).
enum TrashWidgetBridge {
    private static let countKey = "trash_widget.count"
    private static let hasMoreKey = "trash_widget.hasMore"
    private static let purgeAtKey = "trash_widget.purgeAt"

    private static var defaults: UserDefaults? {
        UserDefaults(suiteName: UploadConstants.appGroupID)
    }

    static func update(count: Int, hasMore: Bool, nearestPurgeAt: Date?) {
        guard let defaults else { return }
        defaults.set(count, forKey: countKey)
        defaults.set(hasMore, forKey: hasMoreKey)
        defaults.set(nearestPurgeAt?.timeIntervalSince1970 ?? 0, forKey: purgeAtKey)
        WidgetCenter.shared.reloadTimelines(ofKind: "TrashWidget")
    }
}
