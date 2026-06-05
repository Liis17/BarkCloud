import Foundation
import SwiftProtobuf

/// Одно правило-фильтр умной папки. Поле и оператор переиспользуют proto-enum'ы
/// (`Barkcloud_Files_DfField` / `DfOperator`), значение — строка, которую бэкенд
/// парсит по типу поля (число дней / ISO-дата / байты / px / код MediaKind / текст).
public struct DynamicFolderRule: Identifiable, Hashable, Sendable {
    public let id: UUID
    public var field: Barkcloud_Files_DfField
    public var op: Barkcloud_Files_DfOperator
    public var value: String

    public init(
        id: UUID = UUID(),
        field: Barkcloud_Files_DfField = .dfDate,
        op: Barkcloud_Files_DfOperator = .dfWithinLastDays,
        value: String = ""
    ) {
        self.id = id
        self.field = field
        self.op = op
        self.value = value
    }

    init(_ proto: Barkcloud_Files_DfRule) {
        self.id = UUID()
        self.field = proto.field
        self.op = proto.operator
        self.value = proto.value
    }

    var proto: Barkcloud_Files_DfRule {
        var r = Barkcloud_Files_DfRule()
        r.field = field
        r.operator = op
        r.value = value
        return r
    }
}

/// Карточка умной (динамической) папки — доменная модель UI (зеркало `AlbumCard`).
/// Системные папки (`isSystem`) нельзя редактировать/удалять.
public struct DynamicFolderCard: Identifiable, Hashable, Sendable {
    public let id: String
    public let name: String
    public let isSystem: Bool
    public let combinator: Barkcloud_Files_DfCombinator
    public let rules: [DynamicFolderRule]
    public let iconKey: String
    public let coverColor: String
    public let coverPreviewURL: URL?
    public let itemsCount: Int
    public let viewMode: Barkcloud_Files_DfViewMode
    public let sortOrder: Int

    public init(_ f: Barkcloud_Files_DynamicFolderInfo) {
        self.id = f.id
        self.name = f.name
        self.isSystem = f.isSystem
        self.combinator = f.combinator
        self.rules = f.rules.map(DynamicFolderRule.init)
        self.iconKey = f.iconKey
        self.coverColor = f.coverColor
        self.coverPreviewURL = f.coverPreviewURL.isEmpty ? nil : URL(string: f.coverPreviewURL)
        self.itemsCount = Int(f.itemsCount)
        self.viewMode = f.viewMode
        self.sortOrder = Int(f.sortOrder)
    }
}

/// Страница содержимого умной папки с курсором пагинации.
/// `nextCursorCreatedAt == nil` → страниц больше нет.
public struct DynamicFolderItemsPage: Sendable {
    public let items: [MediaAsset]
    public let nextCursorCreatedAt: Date?
    public let nextCursorFileID: String
    public var hasMore: Bool { nextCursorCreatedAt != nil }
}
