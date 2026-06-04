import Foundation
import FSKit
import BarkCloudKit

/// Том BarkCloud: маппит операции FSKit на облако через `BarkCloudKit`.
///
/// Read-path: монтирование, листинг (`enumerateDirectory`), атрибуты, lookup,
/// поблочное чтение (`RangeBlockReader`). Write-path: create/write/remove/rename/
/// mkdir; байты записи копятся в рабочей копии на диске, реальный upload — на
/// `closeItem` (блобы иммутабельны). Символические/жёсткие ссылки — `ENOTSUP`.
/// ⚠️ Write-семантика не проверена в рантайме (нужен смонтированный том).
///
/// FSKit оперирует нодами (`FSItem`), а не путями — узлы кэшируются в реестре по
/// стабильному `FSItem.Identifier` (инодоподобный id), чтобы lookup/getattr/reclaim
/// ссылались на один и тот же объект.
final class BarkCloudVolume: FSVolume, FSVolume.Operations, FSVolume.PathConfOperations, FSVolume.ReadWriteOperations, FSVolume.OpenCloseOperations {

    private let cloud: CloudRepository
    private let reader: RangeBlockReader
    private let transfer: FileTransferService
    private let root: BarkCloudItem
    private let label: String

    // Реестр узлов под защитой простого замка (FSKit зовёт операции из разных потоков).
    private let lock = NSLock()
    private var nodesByID: [UInt64: BarkCloudItem] = [:]
    private var idByKey: [String: UInt64] = [:]
    private var nextID: UInt64 = 100   // 0/1/2 зарезервированы FSKit

    // Кэш статистики хранилища (used/limit) — `volumeStatistics` синхронный.
    private var cachedUsed: Int64 = 0
    private var cachedLimit: Int64 = 1 << 40   // 1 ТиБ по умолчанию, пока не загрузили

    init(label: String, cloud: CloudRepository, reader: RangeBlockReader, transfer: FileTransferService) {
        self.cloud = cloud
        self.reader = reader
        self.transfer = transfer
        self.label = label
        self.root = BarkCloudItem.makeRoot(label: label)
        super.init(volumeID: FSVolume.Identifier(uuid: UUID()), volumeName: FSFileName(string: label))
        register(root)
    }

    // MARK: - Реестр узлов

    private func register(_ item: BarkCloudItem) {
        lock.lock(); defer { lock.unlock() }
        nodesByID[item.itemID.rawValue] = item
    }

    /// Узел каталога по записи листинга — стабильный id по ключу "d:<dirID>".
    private func dirNode(_ d: CloudDirectory, parent: BarkCloudItem) -> BarkCloudItem {
        node(key: "d:\(d.id)", parent: parent) { id in
            BarkCloudItem.makeDirectory(id: d.id, name: d.name, modified: Date(),
                                        itemID: id, parentID: parent.itemID)
        }
    }

    /// Узел файла по записи листинга — стабильный id по ключу "f:<entryID>".
    private func fileNode(_ f: CloudFileEntry, parent: BarkCloudItem) -> BarkCloudItem {
        node(key: "f:\(f.id)", parent: parent) { id in
            BarkCloudItem.makeFile(entryID: f.id, fileID: f.fileID, name: f.name,
                                   size: f.asset.fileSize, modified: f.asset.uploadedAt ?? f.asset.createdAt,
                                   itemID: id, parentID: parent.itemID, parentDirID: parent.directoryID ?? "")
        }
    }

    private func node(key: String, parent: BarkCloudItem, make: (FSItem.Identifier) -> BarkCloudItem) -> BarkCloudItem {
        lock.lock(); defer { lock.unlock() }
        if let id = idByKey[key], let existing = nodesByID[id] { return existing }
        let id = FSItem.Identifier(rawValue: nextID)!
        nextID += 1
        let item = make(id)
        idByKey[key] = id.rawValue
        nodesByID[id.rawValue] = item
        return item
    }

    /// Выдать новый стабильный id (для свежесозданных, ещё не привязанных файлов).
    private func allocID() -> FSItem.Identifier {
        lock.lock(); defer { lock.unlock() }
        let id = FSItem.Identifier(rawValue: nextID)!
        nextID += 1
        return id
    }

    private func registerKeyed(_ item: BarkCloudItem, key: String) {
        lock.lock(); defer { lock.unlock() }
        idByKey[key] = item.itemID.rawValue
        nodesByID[item.itemID.rawValue] = item
    }

    // MARK: - Рабочие копии записи

    private lazy var workDir: URL = {
        let dir = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("BarkCloud.Drive/work", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }()

    /// Рабочая копия файла на диске (создаётся пустой при первом обращении).
    private func workingFileURL(for node: BarkCloudItem) throws -> URL {
        if let url = node.workingURL { return url }
        let url = workDir.appendingPathComponent("\(node.itemID.rawValue).tmp")
        if !FileManager.default.fileExists(atPath: url.path) {
            FileManager.default.createFile(atPath: url.path, contents: nil)
        }
        node.workingURL = url
        return url
    }

    // MARK: - Жизненный цикл тома

    func activate(options: FSTaskOptions) async throws -> FSItem {
        // Подтянуть квоту для volumeStatistics (не блокируем монтирование при ошибке).
        if let info = try? await transfer.storageInfo() {
            lock.lock(); cachedUsed = info.used; cachedLimit = max(info.limit, 1); lock.unlock()
        }
        return root
    }

    func deactivate(options: FSDeactivateOptions = []) async throws {
        await reader.resetMemory()
        lock.lock(); nodesByID.removeAll(); idByKey.removeAll(); lock.unlock()
        register(root)
    }

    func mount(options: FSTaskOptions) async throws {}
    func unmount() async {}
    func synchronize(flags: FSSyncFlags) async throws {}

    // MARK: - Атрибуты

    func attributes(_ desiredAttributes: FSItem.GetAttributesRequest, of item: FSItem) async throws -> FSItem.Attributes {
        guard let node = item as? BarkCloudItem else { throw Self.posix(EINVAL) }
        return makeAttributes(for: node)
    }

    func setAttributes(_ newAttributes: FSItem.SetAttributesRequest, on item: FSItem) async throws -> FSItem.Attributes {
        // Read-only: молча игнорируем set (FSKit допускает no-op), возвращаем текущие.
        guard let node = item as? BarkCloudItem else { throw Self.posix(EINVAL) }
        return makeAttributes(for: node)
    }

    private func makeAttributes(for node: BarkCloudItem) -> FSItem.Attributes {
        let a = FSItem.Attributes()
        a.type = node.fsType
        a.mode = node.isDirectory ? 0o755 : 0o644
        a.linkCount = 1
        a.size = UInt64(max(node.size, 0))
        a.allocSize = a.size
        a.fileID = node.itemID
        a.parentID = node.parentID
        let t = Self.timespec(from: node.modified)
        a.modifyTime = t
        a.changeTime = t
        a.accessTime = t
        a.birthTime = t
        return a
    }

    // MARK: - Навигация

    func lookupItem(named name: FSFileName, inDirectory directory: FSItem) async throws -> (FSItem, FSFileName) {
        guard let dir = directory as? BarkCloudItem, let dirID = dir.directoryID else {
            throw Self.posix(ENOTDIR)
        }
        guard let target = name.string else { throw Self.posix(ENOENT) }
        let listing = try await cloud.listDirectory(dirID)
        if let d = listing.subdirs.first(where: { $0.name == target }) {
            let n = dirNode(d, parent: dir)
            return (n, FSFileName(string: n.name))
        }
        if let f = listing.files.first(where: { $0.name == target }) {
            let n = fileNode(f, parent: dir)
            return (n, FSFileName(string: n.name))
        }
        throw Self.posix(ENOENT)
    }

    func reclaimItem(_ item: FSItem) async throws {
        guard let node = item as? BarkCloudItem, node.itemID != .rootDirectory else { return }
        lock.lock(); defer { lock.unlock() }
        nodesByID[node.itemID.rawValue] = nil
        idByKey = idByKey.filter { $0.value != node.itemID.rawValue }
    }

    func enumerateDirectory(_ directory: FSItem,
                            startingAt cookie: FSDirectoryCookie,
                            verifier: FSDirectoryVerifier,
                            attributes: FSItem.GetAttributesRequest?,
                            packer: FSDirectoryEntryPacker) async throws -> FSDirectoryVerifier {
        guard let dir = directory as? BarkCloudItem, let dirID = dir.directoryID else {
            throw Self.posix(ENOTDIR)
        }
        let listing = try await cloud.listDirectory(dirID)

        // Единый упорядоченный список: ("." / ".." при attributes==nil) + подкаталоги + файлы.
        // cookie — абсолютный индекс в этом списке (opaque для FSKit).
        var rows: [(name: String, type: FSItem.ItemType, id: FSItem.Identifier, node: BarkCloudItem?)] = []
        if attributes == nil {
            rows.append((".", .directory, dir.itemID, nil))
            rows.append(("..", .directory, dir.parentID, nil))
        }
        for d in listing.subdirs { let n = dirNode(d, parent: dir); rows.append((n.name, .directory, n.itemID, n)) }
        for f in listing.files { let n = fileNode(f, parent: dir); rows.append((n.name, .file, n.itemID, n)) }

        var index = Int(cookie.rawValue)
        while index < rows.count {
            let row = rows[index]
            let attrs: FSItem.Attributes? = (attributes != nil && row.node != nil) ? makeAttributes(for: row.node!) : nil
            let ok = packer.packEntry(name: FSFileName(string: row.name),
                                      itemType: row.type,
                                      itemID: row.id,
                                      nextCookie: FSDirectoryCookie(rawValue: UInt64(index + 1)),
                                      attributes: attrs)
            if !ok { break }   // буфер заполнен — продолжим со следующего cookie
            index += 1
        }
        return FSDirectoryVerifier(rawValue: 1)
    }

    // MARK: - Чтение

    func read(from item: FSItem, at offset: off_t, length: size_t, into buffer: FSMutableFileDataBuffer) async throws -> size_t {
        guard let node = item as? BarkCloudItem, !node.isDirectory else { throw Self.posix(EINVAL) }

        // Несохранённая рабочая копия (новый/редактируемый файл) — читаем с диска.
        if let wurl = node.workingURL {
            let handle = try FileHandle(forReadingFrom: wurl)
            defer { try? handle.close() }
            try handle.seek(toOffset: UInt64(offset))
            let data = try handle.read(upToCount: Int(length)) ?? Data()
            return copy(data, into: buffer)
        }

        guard let fid = node.fileID, !fid.isEmpty else { return 0 }
        let data = try await reader.read(fileID: fid, fileLength: node.size,
                                         offset: Int64(offset), length: Int(length))
        return copy(data, into: buffer)
    }

    private func copy(_ data: Data, into buffer: FSMutableFileDataBuffer) -> size_t {
        guard !data.isEmpty else { return 0 }
        let n = min(data.count, buffer.length)
        buffer.withUnsafeMutableBytes { raw in
            _ = data.copyBytes(to: raw.bindMemory(to: UInt8.self), count: n)
        }
        return size_t(n)
    }

    // MARK: - Запись/мутации (write-path 1.5)
    //
    // Семантика наследуется от Windows-движка: блобы иммутабельны → реальный upload
    // на ЗАКРЫТИИ item (`closeItem`), а не на каждом `write`; до этого байты копятся
    // в рабочей копии на диске. ⚠️ Не проверено в рантайме (нужен смонтированный том).

    func write(contents: Data, to item: FSItem, at offset: off_t) async throws -> size_t {
        guard let node = item as? BarkCloudItem, !node.isDirectory else { throw Self.posix(EINVAL) }
        let url = try workingFileURL(for: node)
        let handle = try FileHandle(forWritingTo: url)
        defer { try? handle.close() }
        try handle.seek(toOffset: UInt64(offset))
        handle.write(contents)
        node.isDirty = true
        node.size = max(node.size, offset + Int64(contents.count))
        return size_t(contents.count)
    }

    func createItem(named name: FSFileName, type: FSItem.ItemType, inDirectory directory: FSItem,
                    attributes newAttributes: FSItem.SetAttributesRequest) async throws -> (FSItem, FSFileName) {
        guard let dir = directory as? BarkCloudItem, let dirID = dir.directoryID else { throw Self.posix(ENOTDIR) }
        let nm = name.string ?? ""
        switch type {
        case .directory:
            let d = try await cloud.createDirectory(parentID: dirID, name: nm)
            let n = dirNode(d, parent: dir)
            return (n, FSFileName(string: n.name))
        case .file:
            // Новый пустой файл: рабочая копия + отложенный upload на закрытии.
            let n = BarkCloudItem.makeFile(entryID: "", fileID: "", name: nm, size: 0,
                                           modified: Date(), itemID: allocID(),
                                           parentID: dir.itemID, parentDirID: dirID)
            _ = try workingFileURL(for: n)
            registerKeyed(n, key: "new:\(dirID)/\(nm)")
            return (n, FSFileName(string: nm))
        default:
            throw Self.posix(ENOTSUP)
        }
    }

    func removeItem(_ item: FSItem, named name: FSFileName, fromDirectory directory: FSItem) async throws {
        guard let node = item as? BarkCloudItem else { throw Self.posix(EINVAL) }
        if let dirID = node.directoryID {
            try await cloud.deleteDirectory(dirID)
        } else if let eid = node.entryID, !eid.isEmpty {
            try await cloud.deleteFileEntry(eid)
        }
        if let w = node.workingURL { try? FileManager.default.removeItem(at: w); node.workingURL = nil }
    }

    func renameItem(_ item: FSItem, inDirectory sourceDirectory: FSItem, named sourceName: FSFileName,
                    to destinationName: FSFileName, inDirectory destinationDirectory: FSItem,
                    overItem: FSItem?) async throws -> FSFileName {
        guard let node = item as? BarkCloudItem,
              let dstDir = destinationDirectory as? BarkCloudItem,
              let dstDirID = dstDir.directoryID else { throw Self.posix(EINVAL) }
        let newName = destinationName.string ?? node.name
        let sameParent = (sourceDirectory as? BarkCloudItem)?.directoryID == dstDirID
        if let dirID = node.directoryID {
            if !sameParent { try await cloud.moveDirectory(dirID, newParentID: dstDirID) }
            if newName != node.name { try await cloud.renameDirectory(dirID, newName: newName) }
        } else if let eid = node.entryID, !eid.isEmpty {
            if !sameParent { try await cloud.moveFileEntry(eid, newDirectoryID: dstDirID) }
            if newName != node.name { try await cloud.renameFileEntry(eid, newName: newName) }
        }
        node.name = newName
        node.parentDirID = dstDirID
        return FSFileName(string: newName)
    }

    // MARK: - Open/Close (триггер upload на закрытии)

    func openItem(_ item: FSItem, modes: FSVolume.OpenModes) async throws {}

    func closeItem(_ item: FSItem, modes: FSVolume.OpenModes) async throws {
        guard let node = item as? BarkCloudItem, !node.isDirectory,
              node.isDirty, let wurl = node.workingURL else { return }
        let data = (try? Data(contentsOf: wurl)) ?? Data()
        // Блоб иммутабелен → перезаливаем целиком и привязываем к родителю.
        let fid = try await cloud.uploadFile(data: data, fileName: node.name, toDirectory: node.parentDirID)
        node.fileID = fid
        node.isDirty = false
    }

    func createSymbolicLink(named name: FSFileName, inDirectory directory: FSItem,
                            attributes newAttributes: FSItem.SetAttributesRequest,
                            linkContents contents: FSFileName) async throws -> (FSItem, FSFileName) {
        throw Self.posix(ENOTSUP)
    }

    func createLink(to item: FSItem, named name: FSFileName, inDirectory directory: FSItem) async throws -> FSFileName {
        throw Self.posix(ENOTSUP)
    }

    func readSymbolicLink(_ item: FSItem) async throws -> FSFileName {
        throw Self.posix(ENOTSUP)
    }

    // MARK: - Возможности тома / статистика / pathconf

    var supportedVolumeCapabilities: FSVolume.SupportedCapabilities {
        let caps = FSVolume.SupportedCapabilities()
        caps.supportsHardLinks = false
        caps.supportsSymbolicLinks = false
        caps.supportsPersistentObjectIDs = true
        caps.caseFormat = .sensitive
        return caps
    }

    var volumeStatistics: FSStatFSResult {
        lock.lock(); let used = cachedUsed; let limit = cachedLimit; lock.unlock()
        let block: UInt64 = 4096
        let result = FSStatFSResult(fileSystemTypeName: "BarkCloud")
        result.blockSize = Int(block)
        result.ioSize = Int(block)
        result.totalBytes = UInt64(max(limit, 0))
        result.usedBytes = UInt64(max(used, 0))
        result.availableBytes = UInt64(max(limit - used, 0))
        result.freeBytes = result.availableBytes
        result.totalBlocks = result.totalBytes / block
        result.usedBlocks = result.usedBytes / block
        result.availableBlocks = result.availableBytes / block
        result.freeBlocks = result.availableBlocks
        return result
    }

    // FSVolume.PathConfOperations
    var maximumLinkCount: Int { 1 }
    var maximumNameLength: Int { 255 }
    var restrictsOwnershipChanges: Bool { false }
    var truncatesLongNames: Bool { false }
    var maximumXattrSize: Int { 0 }
    var maximumFileSize: UInt64 { UInt64(Int64.max) }

    // MARK: - Утилиты

    private static func posix(_ code: Int32) -> NSError {
        NSError(domain: NSPOSIXErrorDomain, code: Int(code))
    }

    private static func timespec(from date: Date) -> timespec {
        let t = date.timeIntervalSince1970
        let sec = floor(t)
        return Foundation.timespec(tv_sec: Int(sec), tv_nsec: Int((t - sec) * 1_000_000_000))
    }
}
