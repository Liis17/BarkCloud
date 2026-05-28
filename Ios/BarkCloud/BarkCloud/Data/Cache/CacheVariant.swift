import Foundation

/// Измерение «что именно кешируем» для одного `fileId`: оригинал или одно из превью.
/// Вместе с `fileId` образует композитный ключ записи (`"{fileId}::{storageKey}"`)
/// и имя файла на диске.
enum CacheVariant: Hashable, Sendable {
    case original
    case preview(width: Int)
    case previewCover
    case avatar
    case avatarPreview

    /// Стабильный строковый идентификатор варианта — часть ключа БД и базовое имя
    /// файла на диске (без расширения).
    var storageKey: String {
        switch self {
        case .original: return "original"
        case .preview(let width): return "preview-\(width)"
        case .previewCover: return "preview-cover"
        case .avatar: return "avatar"
        case .avatarPreview: return "avatar-preview"
        }
    }
}
