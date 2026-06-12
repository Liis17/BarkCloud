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
                completionHandler(BarkCloudFileProviderItem.directory(id: dir.dirID, name: dir.displayName,
                                                                      parent: dir.parentIdentifier,
                                                                      modified: dir.modified), nil)
                return
            }
            if let file = await cache.file(for: identifier) {
                completionHandler(BarkCloudFileProviderItem.file(entryID: file.entryID, fileID: file.fileID,
                                                                 name: file.displayName, size: file.size,
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
                let dest = try await services.transfer.download(from: url, suggestedName: file.displayName)
                let item = BarkCloudFileProviderItem.file(entryID: file.entryID, fileID: file.fileID,
                                                          name: file.displayName, size: file.size,
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
                let isFolder = itemTemplate.contentType?.conforms(to: .folder) ?? false
                let name = itemTemplate.filename

                // Служебные файлы Finder в облако не тянем: система оставит их
                // локальными и не будет ретраить синхронизацию.
                if !isFolder && Self.isFinderJunk(name) {
                    completionHandler(nil, [], false, NSFileProviderError(.excludedFromSync))
                    return
                }

                let services = try await loadServices()

                // Реимпорт после сброса/потери replica: item уже может существовать
                // в облаке. Без проверки upload дедуплицируется по хешу, а attach
                // падает FileAlreadyAttached (инвариант «один блоб — одна запись»).
                if options.contains(.mayAlreadyExist),
                   let existing = try await existingItem(named: name, isFolder: isFolder,
                                                         parentDirID: parentDirID, parent: parent,
                                                         services: services) {
                    completionHandler(existing, [], false, nil)
                    return
                }

                if isFolder {
                    let d = try await services.cloud.createDirectory(parentID: parentDirID, name: name)
                    let local = LocalNameAllocator.sanitize(d.name)
                    await cache.put(directory: d, parent: parent, localName: local)
                    await noteLocalChange(parent: parent)
                    let item = BarkCloudFileProviderItem.directory(id: d.id, name: local, parent: parent,
                                                                   modified: d.updatedAt ?? Date())
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
                    // entryID назад не возвращается — резолвим листингом. Имя могло
                    // быть авто-переименовано бэкендом при коллизии (« (1)»).
                    guard let entry = try await Self.findEntry(in: services.cloud,
                                                               directoryID: parentDirID,
                                                               name: name,
                                                               fileID: fileID) else {
                        completionHandler(nil, [], false, NSFileProviderError(.cannotSynchronize))
                        return
                    }
                    let local = LocalNameAllocator.sanitize(entry.name)
                    await cache.put(file: entry, parentDirID: parentDirID, parent: parent, localName: local)
                    await noteLocalChange(parent: parent)
                    let item = BarkCloudFileProviderItem.file(entryID: entry.id, fileID: entry.fileID,
                                                              name: local, size: entry.asset.fileSize,
                                                              modified: entry.asset.uploadedAt ?? entry.asset.createdAt,
                                                              parent: parent)
                    completionHandler(item, [], false, nil)
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
                    if changedFields.contains(.parentItemIdentifier) {
                        guard let newParentDirID = await parentDirID(for: newParent) else {
                            // Неизвестный родитель (например .trashContainer) —
                            // не рапортуем ложный успех без RPC.
                            completionHandler(nil, [], false, NSFileProviderError(.noSuchItem))
                            return
                        }
                        let oldParentDirID = await parentDirID(for: dir.parentIdentifier)
                        if newParentDirID != oldParentDirID {
                            try await services.cloud.moveDirectory(dir.dirID, newParentID: newParentDirID)
                        }
                    }
                    let renamed = changedFields.contains(.filename) && newName != dir.displayName
                    if renamed {
                        try await services.cloud.renameDirectory(dir.dirID, newName: newName)
                    }
                    // Облачное имя меняется только при rename; при чистом move
                    // сохраняем прежнюю пару cloud/local имён.
                    let cloudName = renamed ? newName : dir.name
                    let localName = renamed ? newName : dir.displayName
                    await cache.putDirectory(dirID: dir.dirID, parent: newParent,
                                             name: cloudName, localName: localName, modified: Date())
                    await noteLocalChange(parent: dir.parentIdentifier, also: newParent)
                    let updated = BarkCloudFileProviderItem.directory(id: dir.dirID, name: localName,
                                                                     parent: newParent, modified: Date())
                    completionHandler(updated, [], false, nil)
                    return
                }

                if let file = await cache.file(for: id) {
                    // Contents-modify: блобы иммутабельны → старая запись в корзину,
                    // новое содержимое — новым entry. При сбое заливки запись
                    // восстанавливается из корзины, чтобы файл не пропал из папки.
                    if changedFields.contains(.contents), let src = newContents {
                        guard let newParentDirID = await parentDirID(for: newParent) else {
                            completionHandler(nil, [], false, NSFileProviderError(.noSuchItem))
                            return
                        }
                        let data = try Data(contentsOf: src)
                        try? await services.cloud.deleteFileEntry(file.entryID)
                        do {
                            let fileID = try await services.cloud.uploadFile(data: data,
                                                                             fileName: newName,
                                                                             toDirectory: newParentDirID)
                            guard let entry = try await Self.findEntry(in: services.cloud,
                                                                       directoryID: newParentDirID,
                                                                       name: newName,
                                                                       fileID: fileID) else {
                                throw NSFileProviderError(.cannotSynchronize)
                            }
                            await cache.forget(id)
                            let local = LocalNameAllocator.sanitize(entry.name)
                            await cache.put(file: entry, parentDirID: newParentDirID,
                                            parent: newParent, localName: local)
                            await noteLocalChange(parent: file.parentIdentifier, also: newParent)
                            let item = BarkCloudFileProviderItem.file(
                                entryID: entry.id, fileID: entry.fileID,
                                name: local, size: entry.asset.fileSize,
                                modified: entry.asset.uploadedAt ?? entry.asset.createdAt,
                                parent: newParent)
                            completionHandler(item, [], false, nil)
                        } catch {
                            try? await services.cloud.restoreFromTrash(entryID: file.entryID)
                            completionHandler(nil, [], false, error)
                        }
                        return
                    }
                    // Только метаданные: rename / move
                    if changedFields.contains(.parentItemIdentifier) {
                        guard let newParentDirID = await parentDirID(for: newParent) else {
                            completionHandler(nil, [], false, NSFileProviderError(.noSuchItem))
                            return
                        }
                        if newParentDirID != file.parentDirID {
                            try await services.cloud.moveFileEntry(file.entryID, newDirectoryID: newParentDirID)
                        }
                    }
                    let renamed = changedFields.contains(.filename) && newName != file.displayName
                    if renamed {
                        try await services.cloud.renameFileEntry(file.entryID, newName: newName)
                    }
                    let cloudName = renamed ? newName : file.name
                    let localName = renamed ? newName : file.displayName
                    let newParentDirID = (await parentDirID(for: newParent)) ?? file.parentDirID
                    await cache.putFile(entryID: file.entryID, fileID: file.fileID,
                                        parentDirID: newParentDirID, parent: newParent,
                                        name: cloudName, localName: localName,
                                        size: file.size, modified: Date(),
                                        previewURL: file.previewURL)
                    await noteLocalChange(parent: file.parentIdentifier, also: newParent)
                    let updated = BarkCloudFileProviderItem.file(entryID: file.entryID, fileID: file.fileID,
                                                                 name: localName, size: file.size,
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

    /// Найти `CloudFileEntry` в листинге директории — после `uploadFile`+`attachFile`
    /// бэкенд не возвращает entryID, а нам он нужен. Ищем по `fileID`: инвариант
    /// бэкенда «один блоб владельца — максимум одна живая запись» делает совпадение
    /// однозначным, а имя могло быть авто-переименовано при коллизии (« (1)»).
    private static func findEntry(in cloud: CloudRepository,
                                  directoryID: String,
                                  name: String,
                                  fileID: String) async throws -> CloudFileEntry? {
        let listing = try await cloud.listDirectory(directoryID)
        if let f = listing.files.first(where: { $0.fileID == fileID }) { return f }
        return listing.files.first(where: { $0.name == name })
    }

    /// Служебные файлы Finder/macOS, бессмысленные в облаке.
    private static func isFinderJunk(_ name: String) -> Bool {
        name == ".DS_Store" || name == ".localized" || name.hasPrefix("._")
    }

    /// Поиск уже существующего в облаке item'а по имени — для `createItem`
    /// с опцией `.mayAlreadyExist` (реимпорт). Сравнение имён — как в APFS:
    /// без регистра, в единой юникод-нормализации.
    private func existingItem(named name: String, isFolder: Bool,
                              parentDirID: String, parent: NSFileProviderItemIdentifier,
                              services: BarkCloudServices) async throws -> BarkCloudFileProviderItem? {
        let key = LocalNameAllocator.collationKey(LocalNameAllocator.sanitize(name))
        func matches(_ cloudName: String) -> Bool {
            LocalNameAllocator.collationKey(LocalNameAllocator.sanitize(cloudName)) == key
        }
        let listing = try await services.cloud.listDirectory(parentDirID)
        if isFolder {
            guard let d = listing.subdirs.first(where: { matches($0.name) }) else { return nil }
            let local = LocalNameAllocator.sanitize(d.name)
            await cache.put(directory: d, parent: parent, localName: local)
            return BarkCloudFileProviderItem.directory(id: d.id, name: local, parent: parent,
                                                       modified: d.updatedAt ?? Date())
        }
        guard let f = listing.files.first(where: { matches($0.name) }) else { return nil }
        let local = LocalNameAllocator.sanitize(f.name)
        await cache.put(file: f, parentDirID: parentDirID, parent: parent, localName: local)
        return BarkCloudFileProviderItem.file(entryID: f.id, fileID: f.fileID, name: local,
                                              size: f.asset.fileSize,
                                              modified: f.asset.uploadedAt ?? f.asset.createdAt,
                                              parent: parent)
    }
}

// MARK: - Миниатюры

/// Миниатюры Finder из облачных превью: бэкенд генерирует превью для фото/видео,
/// их URL кэшируются при enumerate — оригинал для миниатюры не скачивается.
extension BarkCloudFileProvider: NSFileProviderThumbnailing {
    func fetchThumbnails(for itemIdentifiers: [NSFileProviderItemIdentifier],
                         requestedSize size: CGSize,
                         perThumbnailCompletionHandler: @escaping (NSFileProviderItemIdentifier, Data?, Error?) -> Void,
                         completionHandler: @escaping (Error?) -> Void) -> Progress {
        let progress = Progress(totalUnitCount: Int64(itemIdentifiers.count))
        Task {
            for identifier in itemIdentifiers {
                defer { progress.completedUnitCount += 1 }
                guard let file = await cache.file(for: identifier),
                      let raw = file.previewURL,
                      let url = URL(string: raw) else {
                    perThumbnailCompletionHandler(identifier, nil, nil)
                    continue
                }
                do {
                    let (data, response) = try await InsecureHTTP.session.data(from: url)
                    if let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) {
                        perThumbnailCompletionHandler(identifier, data, nil)
                    } else {
                        perThumbnailCompletionHandler(identifier, nil, nil)
                    }
                } catch {
                    perThumbnailCompletionHandler(identifier, nil, error)
                }
            }
            completionHandler(nil)
        }
        return progress
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
