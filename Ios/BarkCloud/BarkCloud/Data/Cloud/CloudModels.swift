import Foundation
import SwiftProtobuf

extension SwiftProtobuf.Google_Protobuf_Timestamp {
    var date: Date {
        Date(timeIntervalSince1970: TimeInterval(seconds) + TimeInterval(nanos) / 1_000_000_000)
    }
}

/// Категория медиа (зеркалит `Barkcloud_Files_MediaKind`).
enum CloudMediaKind: Sendable {
    case other, photo, video, document, audio

    init(_ proto: Barkcloud_Files_MediaKind) {
        switch proto {
        case .photo: self = .photo
        case .video: self = .video
        case .document: self = .document
        case .audio: self = .audio
        default: self = .other
        }
    }

    var isVideo: Bool { self == .video }
}

/// Превью определённой ширины.
struct MediaPreview: Hashable, Sendable {
    let url: URL
    let width: Int
}

/// Медиа-файл облака (фото/видео/документ) с превью и метаданными.
/// `id` — это `file_id` блоба.
struct MediaAsset: Identifiable, Hashable, Sendable {
    let id: String
    let fileName: String
    let fileSize: Int64
    let kind: CloudMediaKind
    let previews: [MediaPreview]
    let createdAt: Date

    init(_ info: Barkcloud_Files_UploadFileInfo) {
        self.id = info.id
        self.fileName = info.fileName
        self.fileSize = info.fileSize
        self.kind = CloudMediaKind(info.mediaKind)
        self.createdAt = info.hasCreatedAt ? info.createdAt.date : Date(timeIntervalSince1970: 0)
        self.previews = info.previews.compactMap { p in
            guard !p.previewURL.isEmpty, let url = URL(string: p.previewURL) else { return nil }
            return MediaPreview(url: url, width: Int(p.targetWidth))
        }
    }

    var isVideo: Bool { kind.isVideo }

    /// Превью ближайшее к нужной ширине (или максимальное доступное) — вместе с его
    /// фактической шириной. Ширина нужна, чтобы один и тот же файл превью получал
    /// один ключ дискового кеша независимо от запрошенной ширины.
    func preview(preferredWidth: Int) -> MediaPreview? {
        guard !previews.isEmpty else { return nil }
        let sorted = previews.sorted { $0.width < $1.width }
        return sorted.first { $0.width >= preferredWidth } ?? sorted.last
    }

    /// Превью ближайшее к нужной ширине (или максимальное доступное).
    func previewURL(preferredWidth: Int) -> URL? {
        preview(preferredWidth: preferredWidth)?.url
    }
}

/// Страница медиа-галереи с курсором пагинации.
struct MediaPage: Sendable {
    let items: [MediaAsset]
    let nextCursorCreatedAt: Date?
    let nextCursorFileID: String
    var hasMore: Bool { nextCursorCreatedAt != nil }
}

/// Папка облака (зеркалит `DirectoryInfo`).
struct CloudDirectory: Identifiable, Hashable, Sendable {
    let id: String
    let parentID: String
    let name: String

    init(_ d: Barkcloud_Files_DirectoryInfo) {
        self.id = d.id
        self.parentID = d.parentID
        self.name = d.name
    }
}

/// Запись о файле в папке (зеркалит `FileEntryDetailed`).
struct CloudFileEntry: Identifiable, Hashable, Sendable {
    let id: String        // entry_id (ID записи в иерархии)
    let fileID: String    // ID блоба
    let name: String
    let asset: MediaAsset

    init(_ d: Barkcloud_Files_FileEntryDetailed) {
        self.id = d.entry.id
        self.fileID = d.entry.fileID
        self.name = d.entry.name
        self.asset = MediaAsset(d.file)
    }
}

/// Содержимое папки.
struct CloudListing: Sendable {
    let subdirs: [CloudDirectory]
    let files: [CloudFileEntry]
}

/// Сегмент хлебных крошек.
struct PathCrumb: Identifiable, Hashable, Sendable {
    let id: String
    let name: String
}

/// Запись в корзине (зеркалит `TrashEntry`). `id` — entry_id записи.
struct TrashItem: Identifiable, Hashable, Sendable {
    let id: String        // entry_id
    let fileID: String    // ID блоба
    let name: String
    let asset: MediaAsset
    let deletedAt: Date
    let purgeAt: Date

    init(_ t: Barkcloud_Files_TrashEntry) {
        self.id = t.entry.id
        self.fileID = t.entry.fileID
        self.name = t.entry.name
        self.asset = MediaAsset(t.file)
        self.deletedAt = t.hasDeletedAt ? t.deletedAt.date : Date(timeIntervalSince1970: 0)
        self.purgeAt = t.hasPurgeAt ? t.purgeAt.date : Date(timeIntervalSince1970: 0)
    }
}

/// Страница корзины с курсором пагинации.
struct TrashPage: Sendable {
    let items: [TrashItem]
    let nextCursorDeletedAt: Date?
    let nextCursorEntryID: String
    var hasMore: Bool { nextCursorDeletedAt != nil }
}

/// Карточка альбома (зеркалит `AlbumInfo`).
struct AlbumCard: Identifiable, Hashable, Sendable {
    let id: String
    let name: String
    let description: String
    let coverPreviewURL: URL?
    let coverFileID: String
    let itemsCount: Int
    let updatedAt: Date

    init(_ a: Barkcloud_Files_AlbumInfo) {
        self.id = a.id
        self.name = a.name
        self.description = a.description_p
        self.coverPreviewURL = a.coverPreviewURL.isEmpty ? nil : URL(string: a.coverPreviewURL)
        self.coverFileID = a.coverFileID
        self.itemsCount = Int(a.itemsCount)
        self.updatedAt = a.hasUpdatedAt ? a.updatedAt.date : Date(timeIntervalSince1970: 0)
    }
}

/// Страница списка альбомов с курсором пагинации.
struct AlbumPage: Sendable {
    let albums: [AlbumCard]
    let nextCursorUpdatedAt: Date?
    let nextCursorAlbumID: String
    var hasMore: Bool { nextCursorUpdatedAt != nil }
}
