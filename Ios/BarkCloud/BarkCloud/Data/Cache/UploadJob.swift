import Foundation
import SwiftData

/// Состояние задачи фоновой загрузки. Хранится в `UploadJob.stateRaw` как строка
/// (SwiftData не любит enum-предикаты, поэтому подменяем строкой).
enum UploadJobState: String, Sendable {
    case pending      // создана, ещё не подана в URLSession
    case preparing    // готовится multipart body / получается uploadURL
    case running      // подана в URLSession, идёт передача байт
    case completed    // 2xx-ответ получен, fileId известен
    case failed       // транспортная ошибка или non-2xx
}

/// Источник задачи. По нему AppEnvironment решает, что делать после успешного
/// завершения (attachFile, скрыть прогресс из Backup-модалки и т.п.).
enum UploadJobSource: String, Sendable {
    case manual   // ручная загрузка (Cloud Browser, Gallery → uploadToCloud)
    case share    // из Share Extension
    case backup   // BackupManager
}

/// Запись фоновой задачи загрузки. Persist в App Group, чтобы переживала перезапуск
/// main app и была видна Share Extension. Жизненный цикл:
/// pending → preparing → running → (completed | failed). Multipart body файл живёт
/// в `multipartBodyPath` до перехода в completed/failed, потом удаляется.
@Model
final class UploadJob {
    @Attribute(.unique) var id: String

    /// `UploadJobSource.rawValue`.
    var sourceKind: String

    /// Путь к исходному файлу в App Group container (оригинал, который грузим).
    var sourceFilePath: String

    /// Путь к подготовленному multipart-body файлу (header + bytes + footer).
    /// Передаётся в `URLSession.uploadTask(with:fromFile:)` — background сессия
    /// принимает только файл, не Data.
    var multipartBodyPath: String

    var fileName: String
    var mimeType: String

    /// Папка в облаке, к которой нужно привязать файл после успешной загрузки
    /// (`CloudApi.AttachFile`). nil = не привязывать (например, медиа без папки).
    var directoryID: String?

    /// `https://.../web/upload/{id}` — выдан `FilesApi.GetUploadUrl`.
    var uploadURL: String

    /// Предварительный file_id, выданный `GetUploadUrl`. После успешной загрузки
    /// может быть заменён на тот, что сервер вернёт в JSON-ответе (учёт дедупликации).
    var preparedFileID: String

    /// `UploadJobState.rawValue`.
    var stateRaw: String

    var bytesSent: Int64
    var totalBytes: Int64

    /// `URLSessionTask.taskIdentifier`. -1 пока задача не подана. Уникален в пределах
    /// одной URLSession (после перезапуска main app переменная остаётся прежней,
    /// но iOS-демон поднимает ту же сессию и matching по identifier работает).
    var sessionTaskIdentifier: Int

    var retries: Int
    var lastError: String?
    var createdAt: Date
    var updatedAt: Date

    init(
        id: String,
        sourceKind: String,
        sourceFilePath: String,
        multipartBodyPath: String,
        fileName: String,
        mimeType: String,
        directoryID: String?,
        uploadURL: String,
        preparedFileID: String,
        stateRaw: String,
        bytesSent: Int64,
        totalBytes: Int64,
        sessionTaskIdentifier: Int,
        retries: Int,
        lastError: String?,
        createdAt: Date,
        updatedAt: Date
    ) {
        self.id = id
        self.sourceKind = sourceKind
        self.sourceFilePath = sourceFilePath
        self.multipartBodyPath = multipartBodyPath
        self.fileName = fileName
        self.mimeType = mimeType
        self.directoryID = directoryID
        self.uploadURL = uploadURL
        self.preparedFileID = preparedFileID
        self.stateRaw = stateRaw
        self.bytesSent = bytesSent
        self.totalBytes = totalBytes
        self.sessionTaskIdentifier = sessionTaskIdentifier
        self.retries = retries
        self.lastError = lastError
        self.createdAt = createdAt
        self.updatedAt = updatedAt
    }
}

extension UploadJob {
    var state: UploadJobState { UploadJobState(rawValue: stateRaw) ?? .pending }
    var source: UploadJobSource { UploadJobSource(rawValue: sourceKind) ?? .manual }
}

/// Sendable-снимок UploadJob — единственный способ доставать данные из
/// actor-стора (`UploadQueueStore`). Содержит все поля, чтобы координатор мог
/// собрать URLRequest без повторного захода в SwiftData.
struct UploadJobSnapshot: Sendable, Identifiable, Hashable {
    let id: String
    let sourceKind: String
    let sourceFilePath: String
    let multipartBodyPath: String
    let fileName: String
    let mimeType: String
    let directoryID: String?
    let uploadURL: String
    let preparedFileID: String
    let state: UploadJobState
    let bytesSent: Int64
    let totalBytes: Int64
    let sessionTaskIdentifier: Int
    let retries: Int
    let lastError: String?
    let createdAt: Date
    let updatedAt: Date

    init(_ job: UploadJob) {
        self.id = job.id
        self.sourceKind = job.sourceKind
        self.sourceFilePath = job.sourceFilePath
        self.multipartBodyPath = job.multipartBodyPath
        self.fileName = job.fileName
        self.mimeType = job.mimeType
        self.directoryID = job.directoryID
        self.uploadURL = job.uploadURL
        self.preparedFileID = job.preparedFileID
        self.state = job.state
        self.bytesSent = job.bytesSent
        self.totalBytes = job.totalBytes
        self.sessionTaskIdentifier = job.sessionTaskIdentifier
        self.retries = job.retries
        self.lastError = job.lastError
        self.createdAt = job.createdAt
        self.updatedAt = job.updatedAt
    }

    var source: UploadJobSource { UploadJobSource(rawValue: sourceKind) ?? .manual }
}
