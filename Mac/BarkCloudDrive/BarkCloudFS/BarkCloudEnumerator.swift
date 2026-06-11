import Foundation
import FileProvider
import BarkCloudKit

/// Превращает облачные имена в допустимые и уникальные имена для macOS внутри
/// одного контейнера. Бэкенд хранит имена как есть (уникальность — байтовая,
/// только среди файлов), а fileproviderd молча отбрасывает item'ы с «/» в имени
/// и с коллизиями имён: ФС регистронезависима и нормализует юникод, плюс файл и
/// папка с одним именем сосуществовать не могут. Такие файлы «не отображаются»
/// в Finder. Коллизии разрешаем суффиксом « (2)», « (3)»… — листинг бэкенда
/// отсортирован по имени, поэтому нумерация стабильна между энумерациями.
struct LocalNameAllocator {
    private var used: Set<String> = []

    /// Ключ сравнения имён, как их сравнивает APFS: без регистра, в единой
    /// юникод-нормализации.
    static func collationKey(_ name: String) -> String {
        name.precomposedStringWithCanonicalMapping.lowercased()
    }

    /// Имя, допустимое для POSIX/Finder: «/» → «:» (Finder отображает «:» как
    /// «/»), без управляющих символов, непустое, не длиннее 255 байт UTF-8.
    static func sanitize(_ raw: String) -> String {
        var name = raw
            .replacingOccurrences(of: "/", with: ":")
            .components(separatedBy: .controlCharacters).joined()
        if name.isEmpty { name = "Без имени" }
        return truncated(name, maxBytes: 255)
    }

    /// Зарезервировать уникальное локальное имя для очередного item'а контейнера.
    mutating func claim(_ raw: String) -> String {
        let base = Self.sanitize(raw)
        var candidate = base
        var n = 2
        while !used.insert(Self.collationKey(candidate)).inserted {
            candidate = Self.numbered(base, n)
            n += 1
        }
        return candidate
    }

    private static func numbered(_ name: String, _ n: Int) -> String {
        let ns = name as NSString
        let ext = ns.pathExtension
        return ext.isEmpty
            ? "\(name) (\(n))"
            : "\(ns.deletingPathExtension) (\(n)).\(ext)"
    }

    private static func truncated(_ name: String, maxBytes: Int) -> String {
        guard name.utf8.count > maxBytes else { return name }
        let ns = name as NSString
        let ext = ns.pathExtension
        let suffix = ext.isEmpty ? "" : ".\(ext)"
        var stem = ns.deletingPathExtension as String
        let budget = maxBytes - suffix.utf8.count
        guard budget > 0 else {
            // Само расширение длиннее лимита — режем имя целиком.
            var whole = name
            while whole.utf8.count > maxBytes { whole.removeLast() }
            return whole
        }
        while stem.utf8.count > budget { stem.removeLast() }
        return stem + suffix
    }
}

/// Перечислитель содержимого папки облака. Один инстанс на контейнер
/// (корень / подпапка). Working-set и trash возвращают пустой набор —
/// инкрементальной синхронизации пока нет (см. `enumerateChanges`).
final class BarkCloudEnumerator: NSObject, NSFileProviderEnumerator {

    /// `""` для корня; иначе `directoryID` папки.
    private let dirID: String
    private let containerIdentifier: NSFileProviderItemIdentifier
    private weak var provider: BarkCloudFileProvider?

    init(containerIdentifier: NSFileProviderItemIdentifier,
         dirID: String,
         provider: BarkCloudFileProvider) {
        self.containerIdentifier = containerIdentifier
        self.dirID = dirID
        self.provider = provider
    }

    func invalidate() {}

    func enumerateItems(for observer: NSFileProviderEnumerationObserver,
                        startingAt page: NSFileProviderPage) {
        guard let provider else {
            observer.finishEnumeratingWithError(NSFileProviderError(.providerNotFound))
            return
        }
        let container = containerIdentifier
        let dir = dirID
        Task {
            do {
                let services = try await provider.loadServices()
                let listing = try await services.cloud.listDirectory(dir)
                var names = LocalNameAllocator()
                var items: [NSFileProviderItem] = []
                items.reserveCapacity(listing.subdirs.count + listing.files.count)
                for d in listing.subdirs {
                    let local = names.claim(d.name)
                    await provider.cache.put(directory: d, parent: container, localName: local)
                    items.append(BarkCloudFileProviderItem.directory(
                        id: d.id, name: local, parent: container,
                        modified: d.updatedAt ?? Date()))
                }
                for f in listing.files {
                    let local = names.claim(f.name)
                    await provider.cache.put(file: f, parentDirID: dir, parent: container, localName: local)
                    items.append(BarkCloudFileProviderItem.file(
                        entryID: f.id, fileID: f.fileID, name: local,
                        size: f.asset.fileSize,
                        modified: f.asset.uploadedAt ?? f.asset.createdAt,
                        parent: container))
                }
                observer.didEnumerate(items)
                observer.finishEnumerating(upTo: nil)
            } catch {
                observer.finishEnumeratingWithError(error)
            }
        }
    }

    func enumerateChanges(for observer: NSFileProviderChangeObserver,
                          from syncAnchor: NSFileProviderSyncAnchor) {
        guard let provider else {
            observer.finishEnumeratingWithError(NSFileProviderError(.providerNotFound))
            return
        }
        Task {
            if await provider.cache.isAnchorCurrent(syncAnchor.rawValue) {
                observer.finishEnumeratingChanges(upTo: syncAnchor, moreComing: false)
            } else {
                // Изменения были (локальные мутации bump'ают anchor) → пусть
                // система сделает полный enumerateItems, мы не ведём change log.
                observer.finishEnumeratingWithError(NSFileProviderError(.syncAnchorExpired))
            }
        }
    }

    func currentSyncAnchor(completionHandler: @escaping (NSFileProviderSyncAnchor?) -> Void) {
        guard let provider else {
            completionHandler(nil)
            return
        }
        Task {
            let data = await provider.cache.currentAnchorData()
            completionHandler(NSFileProviderSyncAnchor(data))
        }
    }
}

/// Пустой перечислитель для working-set / trash (заглушка стадии B).
final class BarkCloudEmptyEnumerator: NSObject, NSFileProviderEnumerator {
    func invalidate() {}
    func enumerateItems(for observer: NSFileProviderEnumerationObserver,
                        startingAt page: NSFileProviderPage) {
        observer.finishEnumerating(upTo: nil)
    }
    func enumerateChanges(for observer: NSFileProviderChangeObserver,
                          from syncAnchor: NSFileProviderSyncAnchor) {
        observer.finishEnumeratingChanges(upTo: syncAnchor, moreComing: false)
    }
}
