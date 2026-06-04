import Foundation
import SwiftProtobuf

extension SwiftProtobuf.Google_Protobuf_Timestamp {
    public var date: Date {
        Date(timeIntervalSince1970: TimeInterval(seconds) + TimeInterval(nanos) / 1_000_000_000)
    }
}

/// Категория медиа (зеркалит `Barkcloud_Files_MediaKind`).
public enum CloudMediaKind: Sendable {
    case other, photo, video, document, audio

    public init(_ proto: Barkcloud_Files_MediaKind) {
        switch proto {
        case .photo: self = .photo
        case .video: self = .video
        case .document: self = .document
        case .audio: self = .audio
        default: self = .other
        }
    }

    public var isVideo: Bool { self == .video }
}

/// Превью определённой ширины.
public struct MediaPreview: Hashable, Sendable {
    public let url: URL
    public let width: Int
}

/// Медиа-файл облака (фото/видео/документ) с превью и метаданными.
/// `id` — это `file_id` блоба.
public struct MediaAsset: Identifiable, Hashable, Sendable {
    public let id: String
    public let fileName: String
    public let fileSize: Int64
    public let kind: CloudMediaKind
    public let previews: [MediaPreview]
    public let createdAt: Date
    public let imageWidth: Int
    public let imageHeight: Int
    public let uploadedAt: Date?
    public let etag: String
    /// Имя устройства, с которого блоб был загружен в первый раз (сохраняется при дедупликации).
    /// Пусто, если бэкенд не передал значение (легаси-файлы до миграции `AddUploadDeviceName`).
    public let uploadDeviceName: String?
    /// file_id полноразмерного JPEG-вида для просмотра (HEIC и пр. браузеро-недружелюбные форматы).
    /// Пусто — нет вида (видео/документ/легаси); тогда показываем оригинал. Для оригинала-JPEG = собственный id.
    public let jpegViewFileID: String

    public init(_ info: Barkcloud_Files_UploadFileInfo) {
        self.id = info.id
        self.fileName = info.fileName
        self.fileSize = info.fileSize
        self.kind = CloudMediaKind(info.mediaKind)
        self.createdAt = info.hasCreatedAt ? info.createdAt.date : Date(timeIntervalSince1970: 0)
        self.imageWidth = Int(info.imageWidth)
        self.imageHeight = Int(info.imageHeight)
        self.uploadedAt = info.hasUploadedAt ? info.uploadedAt.date : nil
        self.etag = info.etag
        self.uploadDeviceName = info.uploadDeviceName.isEmpty ? nil : info.uploadDeviceName
        self.jpegViewFileID = info.jpegViewFileID
        self.previews = info.previews.compactMap { p in
            guard !p.previewURL.isEmpty, let url = URL(string: p.previewURL) else { return nil }
            return MediaPreview(url: url, width: Int(p.targetWidth))
        }
    }

    public var isVideo: Bool { kind.isVideo }

    /// Превью ближайшее к нужной ширине (или максимальное доступное) — вместе с его
    /// фактической шириной. Ширина нужна, чтобы один и тот же файл превью получал
    /// один ключ дискового кеша независимо от запрошенной ширины.
    public func preview(preferredWidth: Int) -> MediaPreview? {
        guard !previews.isEmpty else { return nil }
        let sorted = previews.sorted { $0.width < $1.width }
        return sorted.first { $0.width >= preferredWidth } ?? sorted.last
    }

    /// Превью ближайшее к нужной ширине (или максимальное доступное).
    public func previewURL(preferredWidth: Int) -> URL? {
        preview(preferredWidth: preferredWidth)?.url
    }
}

/// Расширенные метаданные файла (зеркалит `FileMetadataInfo`). Все поля
/// опциональны — отображаются только те, что заполнены сервером.
public struct CloudFileMetadata: Sendable {
    // Общие
    public let takenAt: Date?
    public let creatorTool: String?

    // GPS
    public let latitude: Double?
    public let longitude: Double?
    public let altitude: Double?

    // Камера
    public let cameraMake: String?
    public let cameraModel: String?
    public let lensModel: String?

    // Параметры съёмки
    public let focalLengthMm: Double?
    public let fNumber: Double?
    public let exposureTimeSeconds: Double?
    public let iso: Int?
    public let orientation: Int?
    public let flash: Bool?

    // Видео
    public let durationSeconds: Double?
    public let videoCodec: String?
    public let audioCodec: String?
    public let bitrate: Int64?
    public let frameRate: Double?

    // Документ
    public let documentAuthor: String?
    public let documentTitle: String?
    public let documentSubject: String?
    public let documentPageCount: Int?

    public init(_ m: Barkcloud_Files_FileMetadataInfo) {
        takenAt = m.hasTakenAt ? m.takenAt.date : nil
        creatorTool = m.hasCreatorTool ? m.creatorTool : nil
        latitude = m.hasLatitude ? m.latitude : nil
        longitude = m.hasLongitude ? m.longitude : nil
        altitude = m.hasAltitude ? m.altitude : nil
        cameraMake = m.hasCameraMake ? m.cameraMake : nil
        cameraModel = m.hasCameraModel ? m.cameraModel : nil
        lensModel = m.hasLensModel ? m.lensModel : nil
        focalLengthMm = m.hasFocalLengthMm ? m.focalLengthMm : nil
        fNumber = m.hasFNumber ? m.fNumber : nil
        exposureTimeSeconds = m.hasExposureTimeSeconds ? m.exposureTimeSeconds : nil
        iso = m.hasIso ? Int(m.iso) : nil
        orientation = m.hasOrientation ? Int(m.orientation) : nil
        flash = m.hasFlash ? m.flash : nil
        durationSeconds = m.hasDurationSeconds ? m.durationSeconds : nil
        videoCodec = m.hasVideoCodec ? m.videoCodec : nil
        audioCodec = m.hasAudioCodec ? m.audioCodec : nil
        bitrate = m.hasBitrate ? m.bitrate : nil
        frameRate = m.hasFrameRate ? m.frameRate : nil
        documentAuthor = m.hasDocumentAuthor ? m.documentAuthor : nil
        documentTitle = m.hasDocumentTitle ? m.documentTitle : nil
        documentSubject = m.hasDocumentSubject ? m.documentSubject : nil
        documentPageCount = m.hasDocumentPageCount ? Int(m.documentPageCount) : nil
    }

    public var hasCoordinates: Bool { latitude != nil && longitude != nil }
}

/// Публичная share-ссылка на файл (зеркалит `ShareInfo`). `url` собирается на
/// клиенте: `{webHost}/s/{token}` (бэкенд готовый URL не отдаёт).
public struct ShareLink: Identifiable, Hashable, Sendable {
    public let id: String
    public let token: String
    public let fileID: String
    public let name: String
    public let url: URL?
    public let clickCount: Int
    public let createdAt: Date

    public init(_ info: Barkcloud_Files_ShareInfo) {
        self.id = info.id
        self.token = info.token
        self.fileID = info.fileID
        self.name = info.name
        self.url = GrpcEndpoint.publicShareURL(token: info.token)
        self.clickCount = Int(info.clickCount)
        self.createdAt = info.hasCreatedAt ? info.createdAt.date : Date()
    }
}

/// Страница списка моих публичных ссылок с курсором пагинации.
/// `nextCursorCreatedAt == nil` → больше страниц нет.
public struct ShareLinksPage: Sendable {
    public let items: [ShareLink]
    public let nextCursorCreatedAt: Date?
    public let nextCursorShareID: String
    public var hasMore: Bool { nextCursorCreatedAt != nil }
}

/// Получатель шара / результат поиска пользователей. Зеркалит
/// `Barkcloud_Users_User` в минимальном объёме, нужном для UI выбора (поиск,
/// карточка получателя, отображение «от кого» в Мне доступны).
public struct CloudUser: Identifiable, Hashable, Sendable {
    public let id: Int64
    public let username: String
    public let firstName: String
    public let lastName: String
    public let avatarURL: URL?

    public init(_ u: Barkcloud_Users_User) {
        self.id = u.id
        self.username = u.username
        self.firstName = u.firstName
        self.lastName = u.lastName
        self.avatarURL = URL(string: u.profilePicturePreview.isEmpty ? u.profilePicture : u.profilePicturePreview)
    }

    /// «Имя Фамилия» если есть, иначе `@username`, иначе `id N`.
    public var displayName: String {
        let full = [firstName, lastName].filter { !$0.isEmpty }.joined(separator: " ")
        if !full.isEmpty { return full }
        if !username.isEmpty { return "@\(username)" }
        return "id \(id)"
    }
}

/// Один входящий шар — файл, которым со мной поделился другой пользователь.
/// `file.id` — это `fileID`, по которому через `getSharedFileDownloadUrl`
/// получают временный URL для скачивания. `ownerUserID` потом резолвится в
/// `CloudUser` через `UserRepository.getUser(userID:)`.
public struct SharedFileEntry: Identifiable, Hashable, Sendable {
    public let grantID: String
    public let file: MediaAsset
    public let ownerUserID: Int64
    public let sharedAt: Date

    public init(_ e: Barkcloud_Files_SharedWithMeEntry) {
        self.grantID = e.grantID
        self.file = MediaAsset(e.file)
        self.ownerUserID = e.ownerUserID
        self.sharedAt = e.hasSharedAt ? e.sharedAt.date : Date(timeIntervalSince1970: 0)
    }

    /// `grantID` уникален среди активных грантов и нужен в роли `Identifiable`.
    public var id: String { grantID }
}

/// Страница списка входящих шаров с курсором пагинации.
public struct SharedWithMePage: Sendable {
    public let items: [SharedFileEntry]
    public let nextCursorSharedAt: Date?
    public let nextCursorGrantID: String
    public var hasMore: Bool { nextCursorSharedAt != nil }
}

/// Один исходящий грант — кому конкретно расшарен мой файл. `grantID` — id для
/// `revokeUserShare`. `recipientUserID` резолвится в `CloudUser` через
/// `UserRepository.getUser(userID:)` для отображения имени/аватара.
public struct OutgoingShare: Identifiable, Hashable, Sendable {
    public let grantID: String
    public let recipientUserID: Int64
    public let sharedAt: Date

    public init(_ e: Barkcloud_Files_OutgoingShareEntry) {
        self.grantID = e.grantID
        self.recipientUserID = e.recipientUserID
        self.sharedAt = e.hasSharedAt ? e.sharedAt.date : Date(timeIntervalSince1970: 0)
    }

    public var id: String { grantID }
}

/// Один исходящий грант с полной инфой о файле (зеркалит `OutgoingShareFull`).
/// Бэкенд отдаёт плоский список по всем моим файлам; группировку по файлу для
/// таба «Я поделился» делает клиент. `recipientUserID` резолвится в `CloudUser`.
public struct OutgoingShareFull: Identifiable, Hashable, Sendable {
    public let grantID: String
    public let file: MediaAsset
    public let recipientUserID: Int64
    public let sharedAt: Date

    public init(_ e: Barkcloud_Files_OutgoingShareFull) {
        self.grantID = e.grantID
        self.file = MediaAsset(e.file)
        self.recipientUserID = e.recipientUserID
        self.sharedAt = e.hasSharedAt ? e.sharedAt.date : Date(timeIntervalSince1970: 0)
    }

    public var id: String { grantID }
}

/// Страница плоского списка исходящих грантов с курсором пагинации.
public struct OutgoingSharesAllPage: Sendable {
    public let items: [OutgoingShareFull]
    public let nextCursorSharedAt: Date?
    public let nextCursorGrantID: String
    public var hasMore: Bool { nextCursorSharedAt != nil }
}

/// Страница медиа-галереи с курсором пагинации.
public struct MediaPage: Sendable {
    public let items: [MediaAsset]
    public let nextCursorCreatedAt: Date?
    public let nextCursorFileID: String
    public var hasMore: Bool { nextCursorCreatedAt != nil }
}

/// Папка облака (зеркалит `DirectoryInfo`).
public struct CloudDirectory: Identifiable, Hashable, Sendable {
    public let id: String
    public let parentID: String
    public let name: String

    public init(_ d: Barkcloud_Files_DirectoryInfo) {
        self.id = d.id
        self.parentID = d.parentID
        self.name = d.name
    }
}

/// Запись о файле в папке (зеркалит `FileEntryDetailed`).
public struct CloudFileEntry: Identifiable, Hashable, Sendable {
    public let id: String        // entry_id (ID записи в иерархии)
    public let fileID: String    // ID блоба
    public let name: String
    public let asset: MediaAsset

    public init(_ d: Barkcloud_Files_FileEntryDetailed) {
        self.id = d.entry.id
        self.fileID = d.entry.fileID
        self.name = d.entry.name
        self.asset = MediaAsset(d.file)
    }
}

/// Содержимое папки.
public struct CloudListing: Sendable {
    public let subdirs: [CloudDirectory]
    public let files: [CloudFileEntry]
}

/// Сегмент хлебных крошек.
public struct PathCrumb: Identifiable, Hashable, Sendable {
    public let id: String
    public let name: String
}

/// Запись в корзине (зеркалит `TrashEntry`). `id` — entry_id записи.
public struct TrashItem: Identifiable, Hashable, Sendable {
    public let id: String        // entry_id
    public let fileID: String    // ID блоба
    public let name: String
    public let asset: MediaAsset
    public let deletedAt: Date
    public let purgeAt: Date

    public init(_ t: Barkcloud_Files_TrashEntry) {
        self.id = t.entry.id
        self.fileID = t.entry.fileID
        self.name = t.entry.name
        self.asset = MediaAsset(t.file)
        self.deletedAt = t.hasDeletedAt ? t.deletedAt.date : Date(timeIntervalSince1970: 0)
        self.purgeAt = t.hasPurgeAt ? t.purgeAt.date : Date(timeIntervalSince1970: 0)
    }
}

/// Страница корзины с курсором пагинации.
public struct TrashPage: Sendable {
    public let items: [TrashItem]
    public let nextCursorDeletedAt: Date?
    public let nextCursorEntryID: String
    public var hasMore: Bool { nextCursorDeletedAt != nil }
}

/// Карточка альбома (зеркалит `AlbumInfo`).
public struct AlbumCard: Identifiable, Hashable, Sendable {
    public let id: String
    public let name: String
    public let description: String
    public let coverPreviewURL: URL?
    public let coverFileID: String
    public let itemsCount: Int
    public let updatedAt: Date

    public init(_ a: Barkcloud_Files_AlbumInfo) {
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
public struct AlbumPage: Sendable {
    public let albums: [AlbumCard]
    public let nextCursorUpdatedAt: Date?
    public let nextCursorAlbumID: String
    public var hasMore: Bool { nextCursorUpdatedAt != nil }
}
