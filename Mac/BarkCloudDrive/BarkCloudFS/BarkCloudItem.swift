import Foundation
import FSKit

/// Узел файловой системы BarkCloud. Подкласс `FSItem` (FSKit оперирует нодами, а не
/// строковыми путями — поэтому путевой резолвер Windows-движка тут не нужен).
///
/// Облачная модель: каталог адресуется `directoryID`, файл-запись каталога —
/// `entryID` (+ `fileID` блоба для чтения). Корень — синтетический каталог с
/// `FSItem.Identifier.rootDirectory`.
final class BarkCloudItem: FSItem {
    enum Kind {
        case directory(id: String)   // directoryID ("" — корень на бэкенде)
        case file(entryID: String, fileID: String)
    }

    let kind: Kind
    /// Имя в родительском каталоге (для корня — метка тома).
    var name: String
    /// Размер файла в байтах (для каталога — 0). Берётся из листинга.
    var size: Int64
    /// Время модификации (из листинга), для атрибутов FSKit.
    var modified: Date
    /// Стабильный per-узел id для FSKit (инодоподобный). Корень = rootDirectory.
    let itemID: FSItem.Identifier
    /// id родительского узла (для FSItemAttributes.parentID). Корень → parentOfRoot.
    let parentID: FSItem.Identifier

    init(kind: Kind, name: String, size: Int64, modified: Date,
         itemID: FSItem.Identifier, parentID: FSItem.Identifier) {
        self.kind = kind
        self.name = name
        self.size = size
        self.modified = modified
        self.itemID = itemID
        self.parentID = parentID
        super.init()
    }

    var isDirectory: Bool {
        if case .directory = kind { return true }
        return false
    }

    var fsType: FSItem.ItemType { isDirectory ? .directory : .file }

    /// directoryID для каталога (иначе nil).
    var directoryID: String? {
        if case let .directory(id) = kind { return id }
        return nil
    }

    /// (entryID, fileID) для файла (иначе nil).
    var fileRef: (entryID: String, fileID: String)? {
        if case let .file(entryID, fileID) = kind { return (entryID, fileID) }
        return nil
    }

    /// Корневой каталог тома (directoryID "" = корень на бэкенде BarkCloud).
    static func makeRoot(label: String) -> BarkCloudItem {
        BarkCloudItem(
            kind: .directory(id: ""),
            name: label,
            size: 0,
            modified: Date(),
            itemID: .rootDirectory,
            parentID: .parentOfRoot
        )
    }
}
