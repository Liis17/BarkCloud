import Foundation
import BarkCloudKit

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

    /// Максимальное число повторных попыток фоновой загрузки.
    static let maxUploadRetries = 3

    /// Корень App Group container.
    static var appGroupURL: URL? {
        FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: appGroupID)
    }

    /// Каталог для staged-оригиналов и ограниченных временных part-файлов.
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
        appGroupURL?.appendingPathComponent("UploadSessionQueue.sqlite")
    }

    /// Путь прежней V1-БД. Он никогда не открывается схемой V2: запись удаляется
    /// только в одноразовой миграции после отмены живых V1 URLSession-задач.
    static var legacyUploadQueueDatabaseURL: URL? {
        appGroupURL?.appendingPathComponent("UploadQueue.sqlite")
    }

    static let uploadMigrationMarker = "BarkCloud.upload2.ios.migration.v1"

    static var uploadMigrationCompleted: Bool {
        (UserDefaults(suiteName: appGroupID) ?? .standard).bool(forKey: uploadMigrationMarker)
    }

    static func markUploadMigrationCompleted() {
        (UserDefaults(suiteName: appGroupID) ?? .standard).set(true, forKey: uploadMigrationMarker)
    }

    /// Потоково скопировать файл в staging. Используется fileImporter/Share
    /// Extension и не создаёт `Data` размером с оригинал.
    static func stageFile(_ source: URL, fileName: String? = nil) throws -> URL {
        guard let dir = stagingDirectory else { throw FileTransferError.badURL }
        let safeName = (fileName ?? source.lastPathComponent).isEmpty
            ? UUID().uuidString
            : (fileName ?? source.lastPathComponent)
        let destination = dir.appendingPathComponent("\(UUID().uuidString)-\(safeName)")
        let input = try FileHandle(forReadingFrom: source)
        defer { try? input.close() }
        FileManager.default.createFile(atPath: destination.path, contents: nil)
        let output = try FileHandle(forWritingTo: destination)
        defer { try? output.close() }
        do {
            while let chunk = try input.read(upToCount: 4 * 1024 * 1024), !chunk.isEmpty {
                try output.write(contentsOf: chunk)
            }
        } catch {
            try? FileManager.default.removeItem(at: destination)
            throw error
        }
        return destination
    }

    /// Удалить staged-оригиналы и временные part-файлы — при
    /// полном сбросе, чтобы байты файлов прежнего аккаунта не оставались на диске.
    static func purgeStaging() {
        guard let dir = stagingDirectory else { return }
        let fm = FileManager.default
        guard let items = try? fm.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil) else { return }
        for item in items { try? fm.removeItem(at: item) }
    }

    /// Удалить V1-БД и её sidecar-файлы. Вызывается только до создания первой
    /// V2-задачи; ShareInbox намеренно не затрагивается.
    static func removeLegacyUploadStoreAndStaging() {
        let fm = FileManager.default
        if let legacyURL = legacyUploadQueueDatabaseURL {
            for suffix in ["", "-shm", "-wal"] {
                try? fm.removeItem(at: URL(fileURLWithPath: legacyURL.path + suffix))
            }
        }
        guard let dir = stagingDirectory,
              let items = try? fm.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil) else { return }
        for item in items { try? fm.removeItem(at: item) }
    }

    /// Удалить старые файлы staging, которые больше не принадлежат активным jobs.
    /// Свежие файлы оставляем: их мог только что создать Share Extension, пока
    /// main app ещё не успел увидеть запись в очереди.
    static func purgeOrphanedStaging(
        referencedPaths: Set<String>,
        olderThan age: TimeInterval = 3600
    ) {
        guard let dir = stagingDirectory else { return }
        UploadArtifactCleanup.purgeOrphans(
            in: dir,
            referencedPaths: referencedPaths,
            olderThan: age
        )
    }
}

enum UploadArtifactCleanup {

    static func remove(
        sourcePath: String,
        multipartPath: String,
        within rootURL: URL? = UploadConstants.appGroupURL
    ) {
        let fileManager = FileManager.default
        guard let rootURL else { return }
        let root = rootURL.standardizedFileURL
        let staging = root.appendingPathComponent("UploadStaging", isDirectory: true)
        let shareInbox = root.appendingPathComponent("ShareInbox", isDirectory: true)
        let sourceURL = safeFileURL(sourcePath, allowedRoots: [staging, shareInbox])
        let multipartURL = safeFileURL(multipartPath, allowedRoots: [staging])
        let urls = [sourceURL, multipartURL].compactMap { $0 }
        for url in Set(urls) {
            try? fileManager.removeItem(at: url)
        }
    }

    private static func safeFileURL(_ path: String, allowedRoots: [URL]) -> URL? {
        guard !path.isEmpty else { return nil }
        let url = URL(fileURLWithPath: path).standardizedFileURL
        guard allowedRoots.contains(where: { url.path.hasPrefix($0.path + "/") }) else {
            return nil
        }
        if let isDirectory = try? url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory,
           isDirectory == true {
            return nil
        }
        return url
    }

    static func purgeOrphans(
        in stagingDirectory: URL,
        referencedPaths: Set<String>,
        olderThan age: TimeInterval,
        now: Date = Date()
    ) {
        let fileManager = FileManager.default
        let referenced = Set(referencedPaths.map {
            URL(fileURLWithPath: $0).standardizedFileURL.path
        })
        let cutoff = now.addingTimeInterval(-age)
        guard let items = try? fileManager.contentsOfDirectory(
            at: stagingDirectory,
            includingPropertiesForKeys: [.contentModificationDateKey],
            options: [.skipsHiddenFiles]
        ) else {
            return
        }

        for item in items {
            guard !referenced.contains(item.standardizedFileURL.path),
                  let values = try? item.resourceValues(forKeys: [.contentModificationDateKey]),
                  let modifiedAt = values.contentModificationDate,
                  modifiedAt < cutoff else {
                continue
            }
            try? fileManager.removeItem(at: item)
        }
    }
}

enum TemporaryFileCleanup {

    static func removeFileAndEmptyParent(at fileURL: URL, within rootURL: URL) {
        let fileManager = FileManager.default
        let root = rootURL.standardizedFileURL
        let file = fileURL.standardizedFileURL
        guard file.path.hasPrefix(root.path + "/") else { return }

        try? fileManager.removeItem(at: file)
        guard !fileManager.fileExists(atPath: file.path) else { return }

        let parent = file.deletingLastPathComponent()
        guard parent != root,
              let contents = try? fileManager.contentsOfDirectory(atPath: parent.path),
              contents.isEmpty else {
            return
        }
        try? fileManager.removeItem(at: parent)
    }

    static func purgeStale(
        in directory: URL = FileManager.default.temporaryDirectory,
        olderThan age: TimeInterval = 24 * 3600,
        now: Date = Date()
    ) {
        let fileManager = FileManager.default
        let cutoff = now.addingTimeInterval(-age)
        guard let items = try? fileManager.contentsOfDirectory(
            at: directory,
            includingPropertiesForKeys: [.contentModificationDateKey],
            options: [.skipsHiddenFiles]
        ) else {
            return
        }

        for item in items {
            guard let values = try? item.resourceValues(forKeys: [.contentModificationDateKey]),
                  let modifiedAt = values.contentModificationDate,
                  modifiedAt < cutoff else {
                continue
            }
            try? fileManager.removeItem(at: item)
        }
    }
}
