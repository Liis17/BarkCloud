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
