import Foundation
import WidgetKit

/// Канал передачи состояния корзины из main app в `TrashWidget`. Виджет живёт в
/// отдельном процессе и в gRPC не ходит — приложение кладёт счётчик в App Group
/// `UserDefaults`, а виджет читает его в `TimelineProvider`. Ключи продублированы
/// строками в `TrashSnapshot` виджета — это единственный контракт между таргетами.
///
/// Передаём только количество (`hasMore` — был ли усечён первый лист в 50 записей),
/// без поэлементного дедлайна авто-удаления: список отдаётся в порядке `DeletedAt desc`,
/// поэтому ближайший к удалению (самый старый) элемент лежит на последней странице и
/// дёшево из первого листа не вычисляется. Срок хранения фиксирован
/// (`TrashPurgeService.Retention` = 14 дней) — виджет показывает его статичной подсказкой.
enum TrashWidgetBridge {
    private static let countKey = "trash_widget.count"
    private static let hasMoreKey = "trash_widget.hasMore"

    private static var defaults: UserDefaults? {
        UserDefaults(suiteName: UploadConstants.appGroupID)
    }

    static func update(count: Int, hasMore: Bool) {
        guard let defaults else { return }
        defaults.set(count, forKey: countKey)
        defaults.set(hasMore, forKey: hasMoreKey)
        WidgetCenter.shared.reloadTimelines(ofKind: "TrashWidget")
    }
}
