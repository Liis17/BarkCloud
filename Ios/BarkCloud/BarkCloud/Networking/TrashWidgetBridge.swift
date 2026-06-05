import Foundation
import WidgetKit

/// Канал передачи состояния корзины из main app в `TrashWidget`. Виджет живёт в
/// отдельном процессе и в gRPC не ходит — приложение кладёт счётчик в App Group
/// `UserDefaults`, а виджет читает его в `TimelineProvider`. Ключи продублированы
/// строками в `TrashSnapshot` виджета — это единственный контракт между таргетами.
///
/// Передаём точное число записей и ближайшую дату авто-удаления (самый старый
/// элемент). Оба значения берутся из лёгкого агрегата `CloudApi.GetTrashSummary`
/// (`CloudRepository.trashSummary()`) — серверный `COUNT` + `MIN(PurgeAt)`, поэтому
/// счётчик точный (без «50+»), а дедлайн всегда соответствует самому истекающему файлу.
enum TrashWidgetBridge {
    private static let countKey = "trash_widget.count"
    private static let purgeAtKey = "trash_widget.purgeAt"

    private static var defaults: UserDefaults? {
        UserDefaults(suiteName: UploadConstants.appGroupID)
    }

    static func update(count: Int, oldestPurgeAt: Date?) {
        guard let defaults else { return }
        defaults.set(count, forKey: countKey)
        defaults.set(oldestPurgeAt?.timeIntervalSince1970 ?? 0, forKey: purgeAtKey)
        WidgetCenter.shared.reloadTimelines(ofKind: "TrashWidget")
    }
}
