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
    let imageWidth: Int
    let imageHeight: Int
    let uploadedAt: Date?
    let etag: String
    /// Имя устройства, с которого блоб был загружен в первый раз (сохраняется при дедупликации).
    /// Пусто, если бэкенд не передал значение (легаси-файлы до миграции `AddUploadDeviceName`).
    let uploadDeviceName: String?

    init(_ info: Barkcloud_Files_UploadFileInfo) {
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

/// Расширенные метаданные файла (зеркалит `FileMetadataInfo`). Все поля
/// опциональны — отображаются только те, что заполнены сервером.
struct CloudFileMetadata: Sendable {
    // Общие
    let takenAt: Date?
    let creatorTool: String?

    // GPS
    let latitude: Double?
    let longitude: Double?
    let altitude: Double?

    // Камера
    let cameraMake: String?
    let cameraModel: String?
    let lensModel: String?

    // Параметры съёмки
    let focalLengthMm: Double?
    let fNumber: Double?
    let exposureTimeSeconds: Double?
    let iso: Int?
    let orientation: Int?
    let flash: Bool?

    // Видео
    let durationSeconds: Double?
    let videoCodec: String?
    let audioCodec: String?
    let bitrate: Int64?
    let frameRate: Double?

    // Документ
    let documentAuthor: String?
    let documentTitle: String?
    let documentSubject: String?
    let documentPageCount: Int?

    init(_ m: Barkcloud_Files_FileMetadataInfo) {
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

    var hasCoordinates: Bool { latitude != nil && longitude != nil }
}

/// Публичная share-ссылка на файл (зеркалит `ShareInfo`). `url` собирается на
/// клиенте: `{webHost}/s/{token}` (бэкенд готовый URL не отдаёт).
struct ShareLink: Identifiable, Hashable, Sendable {
    let id: String
    let token: String
    let fileID: String
    let name: String
    let url: URL?
    let clickCount: Int
    let createdAt: Date

    init(_ info: Barkcloud_Files_ShareInfo) {
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
struct ShareLinksPage: Sendable {
    let items: [ShareLink]
    let nextCursorCreatedAt: Date?
    let nextCursorShareID: String
    var hasMore: Bool { nextCursorCreatedAt != nil }
}

/// Получатель шара / результат поиска пользователей. Зеркалит
/// `Barkcloud_Users_User` в минимальном объёме, нужном для UI выбора (поиск,
/// карточка получателя, отображение «от кого» в Мне доступны).
struct CloudUser: Identifiable, Hashable, Sendable {
    let id: Int64
    let username: String
    let firstName: String
    let lastName: String
    let avatarURL: URL?

    init(_ u: Barkcloud_Users_User) {
        self.id = u.id
        self.username = u.username
        self.firstName = u.firstName
        self.lastName = u.lastName
        self.avatarURL = URL(string: u.profilePicturePreview.isEmpty ? u.profilePicture : u.profilePicturePreview)
    }

    /// «Имя Фамилия» если есть, иначе `@username`, иначе `id N`.
    var displayName: String {
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
struct SharedFileEntry: Identifiable, Hashable, Sendable {
    let grantID: String
    let file: MediaAsset
    let ownerUserID: Int64
    let sharedAt: Date

    init(_ e: Barkcloud_Files_SharedWithMeEntry) {
        self.grantID = e.grantID
        self.file = MediaAsset(e.file)
        self.ownerUserID = e.ownerUserID
        self.sharedAt = e.hasSharedAt ? e.sharedAt.date : Date(timeIntervalSince1970: 0)
    }

    /// `grantID` уникален среди активных грантов и нужен в роли `Identifiable`.
    var id: String { grantID }
}

/// Страница списка входящих шаров с курсором пагинации.
struct SharedWithMePage: Sendable {
    let items: [SharedFileEntry]
    let nextCursorSharedAt: Date?
    let nextCursorGrantID: String
    var hasMore: Bool { nextCursorSharedAt != nil }
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
