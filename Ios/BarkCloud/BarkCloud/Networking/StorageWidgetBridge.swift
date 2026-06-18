import Foundation
import WidgetKit

/// Канал передачи состояния хранилища из main app в виджет. Виджет живёт в
/// отдельном процессе и не может ходить в gRPC — поэтому при каждом обновлении
/// приложение кладёт снимок в App Group `UserDefaults`, а виджет
/// читает их в своём `TimelineProvider`. Ключи продублированы строками в
/// `StorageSnapshot` виджета — это единственный контракт между таргетами.
enum StorageWidgetBridge {
    private static let usedKey = "storage_widget.used"
    private static let limitKey = "storage_widget.limit"
    private static let diskTotalKey = "storage_widget.diskTotal"
    private static let diskOtherKey = "storage_widget.diskOther"
    private static let diskS3Key = "storage_widget.diskS3"
    private static let updatedAtKey = "storage_widget.updatedAt"

    private static var defaults: UserDefaults? {
        UserDefaults(suiteName: UploadConstants.appGroupID)
    }

    /// Записать снимок хранилища и попросить систему перерисовать виджеты.
    /// `used/limit` оставлены как fallback для старых данных, основной UI берёт
    /// физический диск: всего, занято не-S3 данными и занято S3.
    static func update(used: Int64, limit: Int64, diskTotal: Int64, diskOther: Int64, diskS3: Int64) {
        guard let defaults else { return }
        defaults.set(used, forKey: usedKey)
        defaults.set(limit, forKey: limitKey)
        defaults.set(diskTotal, forKey: diskTotalKey)
        defaults.set(diskOther, forKey: diskOtherKey)
        defaults.set(diskS3, forKey: diskS3Key)
        defaults.set(Date().timeIntervalSince1970, forKey: updatedAtKey)
        WidgetCenter.shared.reloadTimelines(ofKind: "StorageWidget")
    }
}
