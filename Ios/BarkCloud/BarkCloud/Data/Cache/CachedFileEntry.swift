import Foundation
import SwiftData

/// Запись дискового кеша файла: одна строка на пару `(fileId, variant)`.
/// `key` уникален и совпадает с именем, по которому ищется запись.
/// `relativePath` — путь файла на диске относительно корня кеша.
@Model
final class CachedFileEntry {
    @Attribute(.unique) var key: String
    var fileId: String
    var variant: String
    var sourceURL: String?
    var relativePath: String
    var sizeBytes: Int64
    var lastAccessAt: Date
    var createdAt: Date

    init(
        key: String,
        fileId: String,
        variant: String,
        sourceURL: String?,
        relativePath: String,
        sizeBytes: Int64,
        lastAccessAt: Date,
        createdAt: Date
    ) {
        self.key = key
        self.fileId = fileId
        self.variant = variant
        self.sourceURL = sourceURL
        self.relativePath = relativePath
        self.sizeBytes = sizeBytes
        self.lastAccessAt = lastAccessAt
        self.createdAt = createdAt
    }
}

extension CachedFileEntry {
    /// Композитный ключ записи: `"{fileId}::{variant.storageKey}"`.
    static func key(fileId: String, variant: CacheVariant) -> String {
        "\(fileId)::\(variant.storageKey)"
    }
}
