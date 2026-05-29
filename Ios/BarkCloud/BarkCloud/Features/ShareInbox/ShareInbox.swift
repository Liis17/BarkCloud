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
}
