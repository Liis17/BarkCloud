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

    func createItem(basedOn itemTemplate: NSFileProviderItem,
                    fields: NSFileProviderItemFields,
                    contents url: URL?,
                    options: NSFileProviderCreateItemOptions = [],
                    request: NSFileProviderRequest,
                    completionHandler: @escaping (NSFileProviderItem?, NSFileProviderItemFields, Bool, Error?) -> Void) -> Progress {
        completionHandler(nil, [], false, NSFileProviderError(.serverUnreachable))
        return Progress()
    }

    func modifyItem(_ item: NSFileProviderItem,
                    baseVersion version: NSFileProviderItemVersion,
                    changedFields: NSFileProviderItemFields,
                    contents newContents: URL?,
                    options: NSFileProviderModifyItemOptions = [],
                    request: NSFileProviderRequest,
                    completionHandler: @escaping (NSFileProviderItem?, NSFileProviderItemFields, Bool, Error?) -> Void) -> Progress {
        completionHandler(nil, [], false, NSFileProviderError(.serverUnreachable))
        return Progress()
    }

    func deleteItem(identifier: NSFileProviderItemIdentifier,
                    baseVersion version: NSFileProviderItemVersion,
                    options: NSFileProviderDeleteItemOptions = [],
                    request: NSFileProviderRequest,
                    completionHandler: @escaping (Error?) -> Void) -> Progress {
        completionHandler(NSFileProviderError(.serverUnreachable))
        return Progress()
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
        observer.finishEnumeratingChanges(upTo: syncAnchor, moreComing: false)
    }
}
