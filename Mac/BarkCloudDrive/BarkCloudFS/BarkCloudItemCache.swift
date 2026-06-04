import Foundation
import FileProvider
import BarkCloudKit

/// In-memory кэш ассоциаций «identifier → облачные id», заполняется при
/// enumerate. `fileproviderd` адресует item'ы по `NSFileProviderItemIdentifier`,
/// а API облака — по `directoryID`/`entryID`/`fileID`; кэш закрывает разрыв
/// без обращения к бэкенду в горячем пути.
///
/// При cache miss провайдер отвечает `.noSuchItem`, что заставляет систему
/// перезапросить листинг родителя (см. `BarkCloudFileProvider.item(for:)`).
/// Persistence на диске — задел на будущее (после рестарта расширения кэш
/// пустой; работает за счёт повторного enumerate корня).
actor BarkCloudItemCache {

    struct DirInfo: Sendable {
        let dirID: String
        let parentIdentifier: NSFileProviderItemIdentifier
        let name: String
        let modified: Date
    }

    struct FileInfo: Sendable {
        let entryID: String
        let fileID: String
        let parentDirID: String
        let parentIdentifier: NSFileProviderItemIdentifier
        let name: String
        let size: Int64
        let modified: Date
    }

    private var dirs: [String: DirInfo] = [:]
    private var files: [String: FileInfo] = [:]

    func put(directory d: CloudDirectory, parent: NSFileProviderItemIdentifier) {
        let id = "d:\(d.id)"
        dirs[id] = DirInfo(dirID: d.id, parentIdentifier: parent, name: d.name, modified: Date())
    }

    func put(file f: CloudFileEntry, parentDirID: String, parent: NSFileProviderItemIdentifier) {
        let id = "f:\(f.id)"
        files[id] = FileInfo(entryID: f.id,
                             fileID: f.fileID,
                             parentDirID: parentDirID,
                             parentIdentifier: parent,
                             name: f.name,
                             size: f.asset.fileSize,
                             modified: f.asset.uploadedAt ?? f.asset.createdAt)
    }

    func putDirectory(dirID: String, parent: NSFileProviderItemIdentifier, name: String, modified: Date) {
        dirs["d:\(dirID)"] = DirInfo(dirID: dirID, parentIdentifier: parent, name: name, modified: modified)
    }

    func putFile(entryID: String, fileID: String, parentDirID: String,
                 parent: NSFileProviderItemIdentifier, name: String, size: Int64, modified: Date) {
        files["f:\(entryID)"] = FileInfo(entryID: entryID, fileID: fileID,
                                         parentDirID: parentDirID, parentIdentifier: parent,
                                         name: name, size: size, modified: modified)
    }

    func directory(for identifier: NSFileProviderItemIdentifier) -> DirInfo? {
        dirs[identifier.rawValue]
    }

    func file(for identifier: NSFileProviderItemIdentifier) -> FileInfo? {
        files[identifier.rawValue]
    }

    func forget(_ identifier: NSFileProviderItemIdentifier) {
        dirs.removeValue(forKey: identifier.rawValue)
        files.removeValue(forKey: identifier.rawValue)
    }
}
