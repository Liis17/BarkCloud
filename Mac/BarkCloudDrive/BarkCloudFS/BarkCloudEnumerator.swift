import Foundation
import FileProvider
import BarkCloudKit

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
                var items: [NSFileProviderItem] = []
                items.reserveCapacity(listing.subdirs.count + listing.files.count)
                for d in listing.subdirs {
                    await provider.cache.put(directory: d, parent: container)
                    items.append(BarkCloudFileProviderItem.directory(d, parent: container))
                }
                for f in listing.files {
                    await provider.cache.put(file: f, parentDirID: dir, parent: container)
                    items.append(BarkCloudFileProviderItem.file(f, parent: container))
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
        // Без incremental sync: сообщаем, что изменений нет. Систему это
        // устраивает — она перезапросит enumerateItems по invalidation.
        observer.finishEnumeratingChanges(upTo: syncAnchor, moreComing: false)
    }

    func currentSyncAnchor(completionHandler: @escaping (NSFileProviderSyncAnchor?) -> Void) {
        completionHandler(NSFileProviderSyncAnchor(Data("v1".utf8)))
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
