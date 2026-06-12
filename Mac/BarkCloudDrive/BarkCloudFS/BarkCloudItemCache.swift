import Foundation
import FileProvider
import BarkCloudKit

/// Кэш ассоциаций «identifier → облачные id» с persistent JSON-хранилищем в
/// Application Support песочницы расширения.
///
/// Зачем persistent: `fileproviderd` адресует item'ы по `NSFileProviderItemIdentifier`,
/// сохраняя их между рестартами демона/расширения. После cold-start cache
/// должен ответить на `item(for:)` без обращения к бэкенду, иначе пин/recents
/// в Finder теряют файл (для них нет цепочки enumerate сверху).
///
/// Структура хранения: один JSON-файл `items-cache.json`. Атомарная запись
/// после каждой мутации (для облака до десятков тысяч item'ов нормально;
/// если станет узким местом — заменить на SQLite).
///
/// Sync anchor: монотонный счётчик `UInt64`. Bump'ится при каждой локальной
/// мутации (`createItem`/`modifyItem`/`deleteItem`). `enumerateChanges`
/// сравнивает старый anchor с текущим — при расхождении возвращает
/// `.syncAnchorExpired`, чтобы fileproviderd сделал полный `enumerateItems`.
actor BarkCloudItemCache {

    struct DirInfo: Sendable, Codable {
        let dirID: String
        let parentIdentifierRaw: String
        /// Имя в облаке (как хранит бэкенд).
        let name: String
        /// Имя, показанное в Finder (после санитизации/дедупликации);
        /// `nil` в кэшах, записанных до введения локальных имён.
        let localName: String?
        let modified: Date

        var parentIdentifier: NSFileProviderItemIdentifier {
            NSFileProviderItemIdentifier(parentIdentifierRaw)
        }

        var displayName: String { localName ?? name }
    }

    struct FileInfo: Sendable, Codable {
        let entryID: String
        let fileID: String
        let parentDirID: String
        let parentIdentifierRaw: String
        /// Имя в облаке (как хранит бэкенд).
        let name: String
        /// Имя, показанное в Finder (после санитизации/дедупликации).
        let localName: String?
        let size: Int64
        let modified: Date
        /// URL превью с бэкенда — для миниатюр Finder (`fetchThumbnails`).
        let previewURL: String?

        var parentIdentifier: NSFileProviderItemIdentifier {
            NSFileProviderItemIdentifier(parentIdentifierRaw)
        }

        var displayName: String { localName ?? name }
    }

    private struct Snapshot: Codable {
        var dirs: [String: DirInfo] = [:]
        var files: [String: FileInfo] = [:]
        var anchor: UInt64 = 1
    }

    private var snapshot: Snapshot
    private let storeURL: URL

    init() {
        // Предпочитаем App Group container — он доступен и расширению, и
        // контейнер-app (нужно для очистки cache при logout). Fallback на
        // sandbox-Application Support, если App Group недоступен (например,
        // отсутствует TeamID prefix в Info.plist).
        let dir: URL
        if let container = BarkCloudAppGroup.containerURL {
            dir = container.appendingPathComponent("FileProvider", isDirectory: true)
        } else {
            dir = FileManager.default
                .urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
                .appendingPathComponent("BarkCloud.FileProvider", isDirectory: true)
        }
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        self.storeURL = dir.appendingPathComponent("items-cache.json")
        if let data = try? Data(contentsOf: storeURL),
           let snap = try? JSONDecoder().decode(Snapshot.self, from: data) {
            self.snapshot = snap
        } else {
            self.snapshot = Snapshot()
        }
    }

    // MARK: - Mutators
    //
    // `put*` не bump'ают anchor — они используются и при enumerate (чтение
    // с бэкенда), и при локальных мутациях. Anchor поднимает отдельный
    // `bumpAnchorAndPersist()`, который провайдер вызывает после успешной
    // локальной операции.

    func put(directory d: CloudDirectory, parent: NSFileProviderItemIdentifier, localName: String? = nil) {
        snapshot.dirs["d:\(d.id)"] = DirInfo(
            dirID: d.id,
            parentIdentifierRaw: parent.rawValue,
            name: d.name,
            localName: localName,
            modified: d.updatedAt ?? Date()
        )
        persist()
    }

    func put(file f: CloudFileEntry, parentDirID: String, parent: NSFileProviderItemIdentifier,
             localName: String? = nil) {
        snapshot.files["f:\(f.id)"] = FileInfo(
            entryID: f.id,
            fileID: f.fileID,
            parentDirID: parentDirID,
            parentIdentifierRaw: parent.rawValue,
            name: f.name,
            localName: localName,
            size: f.asset.fileSize,
            modified: f.asset.uploadedAt ?? f.asset.createdAt,
            previewURL: f.asset.previewURL(preferredWidth: 512)?.absoluteString
        )
        persist()
    }

    func putDirectory(dirID: String, parent: NSFileProviderItemIdentifier,
                      name: String, localName: String?, modified: Date) {
        snapshot.dirs["d:\(dirID)"] = DirInfo(
            dirID: dirID,
            parentIdentifierRaw: parent.rawValue,
            name: name,
            localName: localName,
            modified: modified
        )
        persist()
    }

    func putFile(entryID: String, fileID: String, parentDirID: String,
                 parent: NSFileProviderItemIdentifier, name: String, localName: String?,
                 size: Int64, modified: Date, previewURL: String?) {
        snapshot.files["f:\(entryID)"] = FileInfo(
            entryID: entryID,
            fileID: fileID,
            parentDirID: parentDirID,
            parentIdentifierRaw: parent.rawValue,
            name: name,
            localName: localName,
            size: size,
            modified: modified,
            previewURL: previewURL
        )
        persist()
    }

    func forget(_ identifier: NSFileProviderItemIdentifier) {
        let removedDir = snapshot.dirs.removeValue(forKey: identifier.rawValue) != nil
        let removedFile = snapshot.files.removeValue(forKey: identifier.rawValue) != nil
        if removedDir || removedFile { persist() }
    }

    // MARK: - Accessors

    func directory(for identifier: NSFileProviderItemIdentifier) -> DirInfo? {
        snapshot.dirs[identifier.rawValue]
    }

    func file(for identifier: NSFileProviderItemIdentifier) -> FileInfo? {
        snapshot.files[identifier.rawValue]
    }

    // MARK: - Sync anchor

    /// Текущий монотонный anchor (UInt64 big-endian, 8 байт).
    func currentAnchorData() -> Data {
        var value = snapshot.anchor.bigEndian
        return Data(bytes: &value, count: MemoryLayout<UInt64>.size)
    }

    /// `true` если данный anchor равен текущему — тогда изменений нет.
    func isAnchorCurrent(_ data: Data) -> Bool {
        return data == currentAnchorData()
    }

    /// Bump anchor + persist. Вызывается провайдером после успешной локальной
    /// мутации (createItem/modifyItem/deleteItem), чтобы при следующем
    /// `enumerateChanges` система получила `.syncAnchorExpired` и сделала
    /// полный enumerate родителя.
    func bumpAnchorAndPersist() {
        snapshot.anchor &+= 1
        persist()
    }

    private func persist() {
        guard let data = try? JSONEncoder().encode(snapshot) else { return }
        try? data.write(to: storeURL, options: .atomic)
    }

    // MARK: - Reset

    /// Полностью очистить cache (на logout / смену сервера).
    func reset() {
        snapshot = Snapshot()
        persist()
    }
}
