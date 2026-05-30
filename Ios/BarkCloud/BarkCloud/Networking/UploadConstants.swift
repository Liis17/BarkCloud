import Foundation

/// Общие константы для фоновой загрузки. Используются в main app, Share Extension и
/// Widget Extension — поэтому все строковые идентификаторы держим здесь, чтобы
/// один и тот же `URLSession.identifier` совпадал во всех таргетах (тогда iOS-демон
/// объединяет очереди background-задач).
enum UploadConstants {
    /// App Group: общий контейнер main app и Share Extension. Должен совпадать с
    /// `application-groups` в обоих entitlements.
    static let appGroupID = "group.com.barkfluff.BarkCloud"

    /// Идентификатор background `URLSession`. Должен быть одинаков во всех таргетах,
    /// которые ставят задачи в эту сессию (main app + Share Extension).
    static let uploadSessionIdentifier = "com.barkfluff.BarkCloud.upload"

    /// Идентификатор BGTask для retry упавших загрузок. Должен совпадать с
    /// `BGTaskSchedulerPermittedIdentifiers` в Info.plist main app.
    static let retryBGTaskIdentifier = "com.barkfluff.BarkCloud.upload.retry"

    /// Стабильный multipart boundary — пишется и в Content-Type заголовок, и в тело.
    static let multipartBoundary = "BarkCloudUpload-Boundary-7c1f3b2a"

    /// Корень App Group container.
    static var appGroupURL: URL? {
        FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: appGroupID)
    }

    /// Каталог для подготовленных multipart-body и временных копий оригиналов.
    /// Создаётся при первом обращении.
    static var stagingDirectory: URL? {
        guard let appGroupURL else { return nil }
        let dir = appGroupURL.appendingPathComponent("UploadStaging", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    /// URL SwiftData-БД с очередью UploadJob. Лежит в App Group, чтобы был доступен
    /// и main app, и Share Extension.
    static var uploadQueueDatabaseURL: URL? {
        appGroupURL?.appendingPathComponent("UploadQueue.sqlite")
    }

    /// Удалить все подготовленные multipart-body и временные копии оригиналов — при
    /// полном сбросе, чтобы байты файлов прежнего аккаунта не оставались на диске.
    static func purgeStaging() {
        guard let dir = stagingDirectory else { return }
        let fm = FileManager.default
        guard let items = try? fm.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil) else { return }
        for item in items { try? fm.removeItem(at: item) }
    }
}
