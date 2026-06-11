import Foundation
import FileProvider
import UniformTypeIdentifiers

/// `NSFileProviderItem` облака BarkCloud — папка или файл.
///
/// Идентификаторы:
/// - корень — системный `.rootContainer` (соответствует `directoryID == ""` на бэкенде);
/// - папка — `"d:<directoryID>"`;
/// - файл — `"f:<entryID>"`.
///
/// `documentSize` и `contentType` нужны системе для корректного UI в Finder.
/// Версия (`itemVersion`) — две Data-метки: `contentVersion` меняется когда меняется
/// `fileID` (блобы иммутабельны), `metadataVersion` — при ренейме/перемещении.
final class BarkCloudFileProviderItem: NSObject, NSFileProviderItem {

    let itemIdentifier: NSFileProviderItemIdentifier
    let parentItemIdentifier: NSFileProviderItemIdentifier
    let filename: String
    let contentType: UTType
    let documentSize: NSNumber?
    let creationDate: Date?
    let contentModificationDate: Date?
    let itemVersion: NSFileProviderItemVersion
    let capabilities: NSFileProviderItemCapabilities
    let isUploaded: Bool

    private init(itemIdentifier: NSFileProviderItemIdentifier,
                 parentItemIdentifier: NSFileProviderItemIdentifier,
                 filename: String,
                 contentType: UTType,
                 documentSize: NSNumber?,
                 modified: Date?,
                 contentVersion: Data,
                 metadataVersion: Data,
                 capabilities: NSFileProviderItemCapabilities,
                 isUploaded: Bool) {
        self.itemIdentifier = itemIdentifier
        self.parentItemIdentifier = parentItemIdentifier
        self.filename = filename
        self.contentType = contentType
        self.documentSize = documentSize
        self.creationDate = modified
        self.contentModificationDate = modified
        self.itemVersion = NSFileProviderItemVersion(contentVersion: contentVersion,
                                                     metadataVersion: metadataVersion)
        self.capabilities = capabilities
        self.isUploaded = isUploaded
    }

    /// Корневой контейнер тома (`directoryID == ""` на бэкенде). Root нельзя
    /// переименовывать/удалять/перемещать, но в него можно добавлять дочерние.
    static func root() -> BarkCloudFileProviderItem {
        BarkCloudFileProviderItem(
            itemIdentifier: .rootContainer,
            parentItemIdentifier: .rootContainer,
            filename: "BarkCloud",
            contentType: .folder,
            documentSize: nil,
            modified: Date(),
            contentVersion: Data("root".utf8),
            metadataVersion: Data("root".utf8),
            capabilities: [.allowsContentEnumerating, .allowsAddingSubItems],
            isUploaded: true
        )
    }

    static func directory(id: String, name: String,
                          parent: NSFileProviderItemIdentifier,
                          modified: Date) -> BarkCloudFileProviderItem {
        BarkCloudFileProviderItem(
            itemIdentifier: NSFileProviderItemIdentifier("d:\(id)"),
            parentItemIdentifier: parent,
            filename: name,
            contentType: .folder,
            documentSize: nil,
            modified: modified,
            contentVersion: Data("d:\(id)".utf8),
            metadataVersion: Data("\(name)|\(parent.rawValue)".utf8),
            capabilities: [
                .allowsContentEnumerating,
                .allowsAddingSubItems,
                .allowsRenaming,
                .allowsDeleting,
                .allowsReparenting
            ],
            isUploaded: true
        )
    }

    static func file(entryID: String, fileID: String, name: String,
                     size: Int64, modified: Date,
                     parent: NSFileProviderItemIdentifier) -> BarkCloudFileProviderItem {
        let type = Self.contentType(forFilename: name)
        return BarkCloudFileProviderItem(
            itemIdentifier: NSFileProviderItemIdentifier("f:\(entryID)"),
            parentItemIdentifier: parent,
            filename: name,
            contentType: type,
            documentSize: NSNumber(value: size),
            modified: modified,
            contentVersion: Data(fileID.utf8),
            metadataVersion: Data("\(name)|\(parent.rawValue)|\(Int(modified.timeIntervalSinceReferenceDate))".utf8),
            capabilities: [
                .allowsReading,
                .allowsWriting,
                .allowsRenaming,
                .allowsDeleting,
                .allowsReparenting
            ],
            isUploaded: true
        )
    }

    /// UTType из расширения имени, fallback — `data`.
    private static func contentType(forFilename name: String) -> UTType {
        let ext = (name as NSString).pathExtension
        guard !ext.isEmpty, let t = UTType(filenameExtension: ext) else { return .data }
        return t
    }
}
