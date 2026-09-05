import Foundation

/// Общий «почтовый ящик» для файлов из Share Extension.
///
/// Расширение живёт в отдельной песочнице, поэтому единственный канал передачи
/// файлов в приложение — общий контейнер **App Group**. Расширение складывает
/// каждый файл в свою подпапку `<uuid>/<оригинальное-имя>` (имя сохраняется —
/// по расширению бэкенд определяет тип/`MediaKind`), а приложение при следующем
/// запуске забирает их и грузит в облако своей сессией. Токены расширению не нужны.
enum ShareInbox {
    static let appGroupID = "group.com.barkfluff.BarkCloud"
    static let folderName = "ShareInbox"

    static var folderURL: URL? {
        FileManager.default
            .containerURL(forSecurityApplicationGroupIdentifier: appGroupID)?
            .appendingPathComponent(folderName, isDirectory: true)
    }

    /// Файлы, ожидающие загрузки, в порядке поступления (старые → новые).
    /// Каждый элемент — единственный файл внутри своей `<uuid>`-подпапки.
    static func pendingItems() -> [URL] {
        guard let folderURL,
              let subdirs = try? FileManager.default.contentsOfDirectory(
                at: folderURL,
                includingPropertiesForKeys: [.contentModificationDateKey],
                options: [.skipsHiddenFiles])
        else { return [] }

        let sorted = subdirs.sorted { a, b in
            let da = (try? a.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast
            let db = (try? b.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast
            return da < db
        }

        return sorted.compactMap { dir in
            try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles]).first
        }
    }

    /// Удалить загруженный элемент целиком (вместе с его `<uuid>`-подпапкой).
    static func remove(_ item: URL) {
        try? FileManager.default.removeItem(at: item.deletingLastPathComponent())
    }

    /// Legacy-файлы не должны оставаться в App Group навсегда, если аккаунт
    /// недоступен для повторной загрузки. Свежие элементы сохраняем для миграции.
    static func purgeStale(olderThan age: TimeInterval = 30 * 24 * 3600) {
        guard let folderURL,
              let items = try? FileManager.default.contentsOfDirectory(
                at: folderURL,
                includingPropertiesForKeys: [.contentModificationDateKey, .isDirectoryKey],
                options: [.skipsHiddenFiles]
              ) else { return }
        let cutoff = Date.now.addingTimeInterval(-age)
        for item in items {
            guard let values = try? item.resourceValues(
                forKeys: [.contentModificationDateKey, .isDirectoryKey]
            ),
                  values.isDirectory == true,
                  let modifiedAt = values.contentModificationDate,
                  modifiedAt < cutoff else { continue }
            try? FileManager.default.removeItem(at: item)
        }
    }

    /// Полностью удалить legacy-ящик при полном сбросе локального состояния.
    static func purgeAll() {
        guard let folderURL else { return }
        try? FileManager.default.removeItem(at: folderURL)
    }
}
