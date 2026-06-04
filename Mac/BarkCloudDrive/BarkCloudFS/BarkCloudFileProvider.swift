import Foundation
import FileProvider
import BarkCloudKit

/// Сетевые сервисы расширения, поднятые из общей конфигурации (App Group
/// UserDefaults — адрес сервера) и Keychain (refresh-токен). Только
/// Sendable-части — `SessionStore` (@MainActor) удерживается внутри
/// `GrpcManager`, наружу не отдаётся.
struct BarkCloudServices: Sendable {
    let grpc: GrpcManager
    let transfer: FileTransferService
    let cloud: CloudRepository
    let reader: RangeBlockReader
}

/// Точка входа File Provider-расширения (`com.apple.fileprovider-nonui`).
/// Каждый домен (`NSFileProviderDomain`), зарегистрированный контейнер-приложением
/// через `NSFileProviderManager.add(_:completionHandler:)`, поднимает свой
/// инстанс этого класса. Системный демон `fileproviderd` адресует item'ы
/// строковыми `NSFileProviderItemIdentifier`, материализованное содержимое
/// файла отдаётся как обычный POSIX-файл (Range-чтения «на лету», как было
/// у FSKit/FUSE, нет — файл качается целиком и кэшируется системой).
///
/// Стадия B (read-path): item/enumerator/fetchContents через `CloudRepository`
/// и `FileTransferService.tempDownloadURLs`. Write-path — следующая стадия.
final class BarkCloudFileProvider: NSObject, NSFileProviderReplicatedExtension {

    let domain: NSFileProviderDomain

    /// In-memory кэш `identifier → облачные id`, заполняется при enumerate.
    let cache = BarkCloudItemCache()

    /// Сетевой слой `BarkCloudKit` — ленивая инициализация на MainActor
    /// (`SessionStore` помечен @MainActor для UI-обвязки в iOS-таргете).
    @MainActor private var cachedServices: BarkCloudServices?

    required init(domain: NSFileProviderDomain) {
        self.domain = domain
        super.init()
    }

    func invalidate() {}

    /// Ленивый сетевой слой: gRPC-клиенты, сессия из App Group + Keychain.
    /// `SessionStore` (@MainActor) удерживается через `GrpcManager` — на
    /// общие методы можно вызывать с любого актора.
    @MainActor
    func loadServices() throws -> BarkCloudServices {
        if let s = cachedServices { return s }
        let store = SessionStore()
        let grpc = GrpcManager(session: store)
        let transfer = FileTransferService(grpc: grpc)
        let cloud = CloudRepository(grpc: grpc, transfer: transfer)
        let cacheDir = FileManager.default
            .urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("BarkCloud.Drive", isDirectory: true)
        let reader = RangeBlockReader(transfer: transfer, cacheDir: cacheDir)
        let s = BarkCloudServices(grpc: grpc, transfer: transfer, cloud: cloud, reader: reader)
        cachedServices = s
        return s
    }

    // MARK: - item / enumerator

    func item(for identifier: NSFileProviderItemIdentifier,
              request: NSFileProviderRequest,
              completionHandler: @escaping (NSFileProviderItem?, Error?) -> Void) -> Progress {
        Task {
            if identifier == .rootContainer {
                completionHandler(BarkCloudFileProviderItem.root(), nil)
                return
            }
            if let dir = await cache.directory(for: identifier) {
                completionHandler(BarkCloudFileProviderItem.directory(id: dir.dirID, name: dir.name,
                                                                      parent: dir.parentIdentifier,
                                                                      modified: dir.modified), nil)
                return
            }
            if let file = await cache.file(for: identifier) {
                completionHandler(BarkCloudFileProviderItem.file(entryID: file.entryID, fileID: file.fileID,
                                                                 name: file.name, size: file.size,
                                                                 modified: file.modified,
                                                                 parent: file.parentIdentifier), nil)
                return
            }
            completionHandler(nil, NSFileProviderError(.noSuchItem))
        }
        return Progress()
    }

    func enumerator(for containerItemIdentifier: NSFileProviderItemIdentifier,
                    request: NSFileProviderRequest) throws -> NSFileProviderEnumerator {
        if containerItemIdentifier == .workingSet || containerItemIdentifier == .trashContainer {
            return BarkCloudEmptyEnumerator()
        }
        if containerItemIdentifier == .rootContainer {
            return BarkCloudEnumerator(containerIdentifier: .rootContainer, dirID: "", provider: self)
        }
        // Подпапка — берём dirID из кэша.
        // enumerator(for:) синхронный; пробуем sync через actor unsafe semaphore — нет,
        // нельзя; вместо этого даём enumerator с dirID, который он сам разрешит
        // через cache (а если не найдёт — finishEnumeratingWithError).
        return BarkCloudPendingEnumerator(containerIdentifier: containerItemIdentifier, provider: self)
    }

    // MARK: - fetchContents

    func fetchContents(for itemIdentifier: NSFileProviderItemIdentifier,
                       version requestedVersion: NSFileProviderItemVersion?,
                       request: NSFileProviderRequest,
                       completionHandler: @escaping (URL?, NSFileProviderItem?, Error?) -> Void) -> Progress {
        let progress = Progress(totalUnitCount: 1)
        Task {
            guard let file = await cache.file(for: itemIdentifier) else {
                completionHandler(nil, nil, NSFileProviderError(.noSuchItem))
                return
            }
            do {
                let services = try await loadServices()
                let urls = try await services.transfer.tempDownloadURLs(fileIDs: [file.fileID])
                guard let url = urls[file.fileID] else {
                    completionHandler(nil, nil, NSFileProviderError(.serverUnreachable))
                    return
                }
                let dest = try await services.transfer.download(from: url, suggestedName: file.name)
                let item = BarkCloudFileProviderItem.file(entryID: file.entryID, fileID: file.fileID,
                                                          name: file.name, size: file.size,
                                                          modified: file.modified,
                                                          parent: file.parentIdentifier)
                progress.completedUnitCount = 1
                completionHandler(dest, item, nil)
            } catch {
                completionHandler(nil, nil, error)
            }
        }
        return progress
    }

    // MARK: - write-path (стадия C)
    //
    // Семантика: блобы иммутабельны → modify contents = удалить старый entry +
    // upload+attach как новый (новый entryID/identifier; fileproviderd подменит).
    // Rename/move папок и файлов — отдельные RPC `rename*`/`move*`.

    func createItem(basedOn itemTemplate: NSFileProviderItem,
                    fields: NSFileProviderItemFields,
                    contents url: URL?,
                    options: NSFileProviderCreateItemOptions = [],
                    request: NSFileProviderRequest,
                    completionHandler: @escaping (NSFileProviderItem?, NSFileProviderItemFields, Bool, Error?) -> Void) -> Progress {
        Task {
            do {
                let parent = itemTemplate.parentItemIdentifier
                guard let parentDirID = await parentDirID(for: parent) else {
                    completionHandler(nil, [], false, NSFileProviderError(.noSuchItem))
                    return
                }
                let services = try await loadServices()
                let isFolder = itemTemplate.contentType?.conforms(to: .folder) ?? false
                let name = itemTemplate.filename
                if isFolder {
                    let d = try await services.cloud.createDirectory(parentID: parentDirID, name: name)
                    await cache.put(directory: d, parent: parent)
                    await noteLocalChange(parent: parent)
                    let item = BarkCloudFileProviderItem.directory(d, parent: parent)
                    completionHandler(item, [], false, nil)
                } else {
                    guard let src = url else {
                        completionHandler(nil, [], false, NSFileProviderError(.noSuchItem))
                        return
                    }
                    let data = try Data(contentsOf: src)
                    let fileID = try await services.cloud.uploadFile(data: data,
                                                                     fileName: name,
                                                                     toDirectory: parentDirID)
                    // entryID назад не возвращается — резолвим листингом.
                    guard let entry = try await Self.findEntry(in: services.cloud,
                                                               directoryID: parentDirID,
                                                               name: name,
                                                               fileID: fileID) else {
                        completionHandler(nil, [], false, NSFileProviderError(.cannotSynchronize))
                        return
                    }
                    await cache.put(file: entry, parentDirID: parentDirID, parent: parent)
                    await noteLocalChange(parent: parent)
                    completionHandler(BarkCloudFileProviderItem.file(entry, parent: parent), [], false, nil)
                }
            } catch {
                completionHandler(nil, [], false, error)
            }
        }
        return Progress()
    }

    func modifyItem(_ item: NSFileProviderItem,
                    baseVersion version: NSFileProviderItemVersion,
                    changedFields: NSFileProviderItemFields,
                    contents newContents: URL?,
                    options: NSFileProviderModifyItemOptions = [],
                    request: NSFileProviderRequest,
                    completionHandler: @escaping (NSFileProviderItem?, NSFileProviderItemFields, Bool, Error?) -> Void) -> Progress {
        Task {
            do {
                let services = try await loadServices()
                let id = item.itemIdentifier
                let newName = item.filename
                let newParent = item.parentItemIdentifier

                if let dir = await cache.directory(for: id) {
                    if changedFields.contains(.parentItemIdentifier),
                       let newParentDirID = await parentDirID(for: newParent),
                       newParentDirID != dir.parentIdentifier.rawValue {
                        try await services.cloud.moveDirectory(dir.dirID, newParentID: newParentDirID)
                    }
                    if changedFields.contains(.filename) && newName != dir.name {
                        try await services.cloud.renameDirectory(dir.dirID, newName: newName)
                    }
                    await cache.putDirectory(dirID: dir.dirID, parent: newParent, name: newName, modified: Date())
                    await noteLocalChange(parent: dir.parentIdentifier, also: newParent)
                    let updated = BarkCloudFileProviderItem.directory(id: dir.dirID, name: newName,
                                                                     parent: newParent, modified: Date())
                    completionHandler(updated, [], false, nil)
                    return
                }

                if let file = await cache.file(for: id) {
                    // Contents-modify (блобы иммутабельны) → delete old + upload as new
                    if changedFields.contains(.contents), let src = newContents,
                       let newParentDirID = await parentDirID(for: newParent) {
                        let data = try Data(contentsOf: src)
                        try? await services.cloud.deleteFileEntry(file.entryID)
                        await cache.forget(id)
                        let fileID = try await services.cloud.uploadFile(data: data,
                                                                         fileName: newName,
                                                                         toDirectory: newParentDirID)
                        guard let entry = try await Self.findEntry(in: services.cloud,
                                                                   directoryID: newParentDirID,
                                                                   name: newName,
                                                                   fileID: fileID) else {
                            completionHandler(nil, [], false, NSFileProviderError(.cannotSynchronize))
                            return
                        }
                        await cache.put(file: entry, parentDirID: newParentDirID, parent: newParent)
                        await noteLocalChange(parent: file.parentIdentifier, also: newParent)
                        completionHandler(BarkCloudFileProviderItem.file(entry, parent: newParent), [], false, nil)
                        return
                    }
                    // Только метаданные: rename / move
                    if changedFields.contains(.parentItemIdentifier),
                       let newParentDirID = await parentDirID(for: newParent),
                       newParentDirID != file.parentDirID {
                        try await services.cloud.moveFileEntry(file.entryID, newDirectoryID: newParentDirID)
                    }
                    if changedFields.contains(.filename) && newName != file.name {
                        try await services.cloud.renameFileEntry(file.entryID, newName: newName)
                    }
                    let newParentDirID = (await parentDirID(for: newParent)) ?? file.parentDirID
                    await cache.putFile(entryID: file.entryID, fileID: file.fileID,
                                        parentDirID: newParentDirID, parent: newParent,
                                        name: newName, size: file.size, modified: Date())
                    await noteLocalChange(parent: file.parentIdentifier, also: newParent)
                    let updated = BarkCloudFileProviderItem.file(entryID: file.entryID, fileID: file.fileID,
                                                                 name: newName, size: file.size,
                                                                 modified: Date(), parent: newParent)
                    completionHandler(updated, [], false, nil)
                    return
                }

                completionHandler(nil, [], false, NSFileProviderError(.noSuchItem))
            } catch {
                completionHandler(nil, [], false, error)
            }
        }
        return Progress()
    }

    func deleteItem(identifier: NSFileProviderItemIdentifier,
                    baseVersion version: NSFileProviderItemVersion,
                    options: NSFileProviderDeleteItemOptions = [],
                    request: NSFileProviderRequest,
                    completionHandler: @escaping (Error?) -> Void) -> Progress {
        Task {
            do {
                let services = try await loadServices()
                if let dir = await cache.directory(for: identifier) {
                    try await services.cloud.deleteDirectory(dir.dirID)
                    await cache.forget(identifier)
                    await noteLocalChange(parent: dir.parentIdentifier)
                    completionHandler(nil)
                    return
                }
                if let file = await cache.file(for: identifier) {
                    try await services.cloud.deleteFileEntry(file.entryID)
                    await cache.forget(identifier)
                    await noteLocalChange(parent: file.parentIdentifier)
                    completionHandler(nil)
                    return
                }
                completionHandler(NSFileProviderError(.noSuchItem))
            } catch {
                completionHandler(error)
            }
        }
        return Progress()
    }

    // MARK: - утилиты

    private func parentDirID(for parent: NSFileProviderItemIdentifier) async -> String? {
        if parent == .rootContainer { return "" }
        return await cache.directory(for: parent)?.dirID
    }

    /// Локальная мутация прошла успешно — bump cache anchor и просигналить
    /// fileproviderd о затронутых контейнерах (может быть до двух — старый и
    /// новый родитель при move). Игнорируем ошибки сигнала: incremental sync
    /// — оптимизация, без неё система всё равно увидит изменения при следующем
    /// `enumerateItems`.
    func noteLocalChange(parent: NSFileProviderItemIdentifier,
                         also otherParent: NSFileProviderItemIdentifier? = nil) async {
        await cache.bumpAnchorAndPersist()
        guard let manager = NSFileProviderManager(for: domain) else { return }
        try? await manager.signalEnumerator(for: parent)
        if let other = otherParent, other != parent {
            try? await manager.signalEnumerator(for: other)
        }
    }

    /// Найти `CloudFileEntry` в листинге директории по имени и (опционально) fileID
    /// — после `uploadFile`+`attachFile` бэкенд не возвращает entryID, а нам он нужен.
    private static func findEntry(in cloud: CloudRepository,
                                  directoryID: String,
                                  name: String,
                                  fileID: String) async throws -> CloudFileEntry? {
        let listing = try await cloud.listDirectory(directoryID)
        if let f = listing.files.first(where: { $0.fileID == fileID && $0.name == name }) { return f }
        return listing.files.first(where: { $0.name == name })
    }
}

/// Перечислитель, разрешающий `dirID` подпапки из кэша на старте enumerate.
/// `enumerator(for:)` синхронный, а кэш — `actor`; обёртка переносит
/// разрешение dirID в асинхронный путь.
final class BarkCloudPendingEnumerator: NSObject, NSFileProviderEnumerator {
    private let containerIdentifier: NSFileProviderItemIdentifier
    private weak var provider: BarkCloudFileProvider?

    init(containerIdentifier: NSFileProviderItemIdentifier, provider: BarkCloudFileProvider) {
        self.containerIdentifier = containerIdentifier
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
        Task {
            guard let dir = await provider.cache.directory(for: container) else {
                observer.finishEnumeratingWithError(NSFileProviderError(.noSuchItem))
                return
            }
            let real = BarkCloudEnumerator(containerIdentifier: container, dirID: dir.dirID, provider: provider)
            real.enumerateItems(for: observer, startingAt: page)
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
