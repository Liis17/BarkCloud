import Foundation
import FSKit

/// Узел файловой системы BarkCloud. Подкласс `FSItem` (FSKit оперирует нодами, а не
/// строковыми путями — поэтому путевой резолвер Windows-движка тут не нужен).
///
/// Облачная модель: каталог адресуется `directoryID`, файл-запись каталога —
/// `entryID` (+ `fileID` блоба для чтения). Корень — синтетический каталог с
/// `FSItem.Identifier.rootDirectory`. Идентичность файла **изменяема**: новый файл
/// создаётся без `fileID`, а после upload на закрытии получает реальные id.
final class BarkCloudItem: FSItem {
    /// Каталог: directoryID (для файла nil). "" — корень на бэкенде.
    var directoryID: String?
    /// Файл: entryID записи в иерархии (пусто, пока не привязан).
    var entryID: String?
    /// Файл: fileID блоба (пусто, пока не загружен).
    var fileID: String?

    /// Имя в родительском каталоге (для корня — метка тома).
    var name: String
    /// Размер файла в байтах (для каталога — 0). Берётся из листинга / рабочей копии.
    var size: Int64
    /// Время модификации (из листинга), для атрибутов FSKit.
    var modified: Date
    /// Стабильный per-узел id для FSKit (инодоподобный). Корень = rootDirectory.
    let itemID: FSItem.Identifier
    /// id родительского узла (для FSItemAttributes.parentID). Корень → parentOfRoot.
    let parentID: FSItem.Identifier

    // --- Состояние записи (write-path) ---
    /// Рабочая копия на диске для записи (буфер до upload на закрытии).
    var workingURL: URL?
    /// Содержимое менялось с момента открытия → на закрытии перезалить.
    var isDirty: Bool = false
    /// directoryID родителя — куда привязывать файл при upload на закрытии.
    var parentDirID: String?

    init(directoryID: String?, entryID: String?, fileID: String?,
         name: String, size: Int64, modified: Date,
         itemID: FSItem.Identifier, parentID: FSItem.Identifier, parentDirID: String? = nil) {
        self.directoryID = directoryID
        self.entryID = entryID
        self.fileID = fileID
        self.name = name
        self.size = size
        self.modified = modified
        self.itemID = itemID
        self.parentID = parentID
        self.parentDirID = parentDirID
        super.init()
    }

    var isDirectory: Bool { directoryID != nil }
    var fsType: FSItem.ItemType { isDirectory ? .directory : .file }

    static func makeDirectory(id: String, name: String, modified: Date,
                              itemID: FSItem.Identifier, parentID: FSItem.Identifier) -> BarkCloudItem {
        BarkCloudItem(directoryID: id, entryID: nil, fileID: nil, name: name, size: 0,
                      modified: modified, itemID: itemID, parentID: parentID)
    }

    static func makeFile(entryID: String, fileID: String, name: String, size: Int64, modified: Date,
                         itemID: FSItem.Identifier, parentID: FSItem.Identifier, parentDirID: String) -> BarkCloudItem {
        BarkCloudItem(directoryID: nil, entryID: entryID, fileID: fileID, name: name, size: size,
                      modified: modified, itemID: itemID, parentID: parentID, parentDirID: parentDirID)
    }

    /// Корневой каталог тома (directoryID "" = корень на бэкенде BarkCloud).
    static func makeRoot(label: String) -> BarkCloudItem {
        BarkCloudItem(directoryID: "", entryID: nil, fileID: nil, name: label, size: 0,
                      modified: Date(), itemID: .rootDirectory, parentID: .parentOfRoot)
    }
}
