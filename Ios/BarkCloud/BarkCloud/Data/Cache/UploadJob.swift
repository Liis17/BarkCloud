import Foundation
import SwiftData

/// Жизненный цикл Upload 2.0. Старые состояния `preparing`/`running` сохранены
/// для чтения записей от предыдущей версии, но новые задачи используют только
/// состояния начиная с `hashing`.
enum UploadJobState: String, Sendable {
    case pending
    case preparing
    case running
    case hashing
    case creatingSession
    case uploading
    case completing
    case processing
    case attaching
    case uploadedNotAttached
    case completed
    case failed
    case cancelled

    var isActive: Bool {
        switch self {
        case .pending, .preparing, .running, .hashing, .creatingSession,
             .uploading, .completing, .processing, .attaching,
             .uploadedNotAttached:
            return true
        case .completed, .failed, .cancelled:
            return false
        }
    }

    var isTerminal: Bool {
        switch self {
        case .completed, .failed, .cancelled: return true
        default: return false
        }
    }
}

enum UploadJobSource: String, Sendable {
    case manual
    case share
    case backup
}

/// Действие, которое выполняется только после серверного `ready`.
enum UploadPostReadyIntent: String, Sendable {
    case none
    case attachDirectory
    case routeByMediaKind
    case addToAlbum
}

@Model
final class UploadSessionJob {
    @Attribute(.unique) var id: String

    var sourceKind: String
    var sourceFilePath: String
    var fileName: String
    var mimeType: String
    var directoryID: String?

    var stateRaw: String
    var bytesSent: Int64
    var totalBytes: Int64
    var sessionTaskIdentifier: Int
    var retries: Int
    var retryable: Bool
    var lastError: String?
    var createdAt: Date
    var updatedAt: Date

    // Upload 2.0 persisted metadata. Upload token намеренно отсутствует.
    var idempotencyKey: String
    var sessionID: String
    var fileID: String
    var sha256: String
    var partSize: Int64
    var currentPart: Int
    var intentRaw: String
    var albumID: String?
    var localIdentifier: String?

    init(
        id: String,
        sourceKind: String,
        sourceFilePath: String,
        fileName: String,
        mimeType: String,
        directoryID: String?,
        stateRaw: String,
        bytesSent: Int64,
        totalBytes: Int64,
        sessionTaskIdentifier: Int = -1,
        retries: Int = 0,
        retryable: Bool = true,
        lastError: String? = nil,
        createdAt: Date = .now,
        updatedAt: Date = .now,
        idempotencyKey: String = "",
        sessionID: String = "",
        fileID: String = "",
        sha256: String = "",
        partSize: Int64 = 0,
        currentPart: Int = 0,
        intentRaw: String = UploadPostReadyIntent.none.rawValue,
        albumID: String? = nil,
        localIdentifier: String? = nil
    ) {
        self.id = id
        self.sourceKind = sourceKind
        self.sourceFilePath = sourceFilePath
        self.fileName = fileName
        self.mimeType = mimeType
        self.directoryID = directoryID
        self.stateRaw = stateRaw
        self.bytesSent = bytesSent
        self.totalBytes = totalBytes
        self.sessionTaskIdentifier = sessionTaskIdentifier
        self.retries = retries
        self.retryable = retryable
        self.lastError = lastError
        self.createdAt = createdAt
        self.updatedAt = updatedAt
        self.idempotencyKey = idempotencyKey
        self.sessionID = sessionID
        self.fileID = fileID
        self.sha256 = sha256
        self.partSize = partSize
        self.currentPart = currentPart
        self.intentRaw = intentRaw
        self.albumID = albumID
        self.localIdentifier = localIdentifier
    }
}

/// Совместимое имя для существующего UI/координатора. Фактическая SwiftData
/// модель и доменная очередь называются `UploadSessionJob`.
typealias UploadJob = UploadSessionJob

extension UploadJob {
    var state: UploadJobState { UploadJobState(rawValue: stateRaw) ?? .pending }
    var source: UploadJobSource { UploadJobSource(rawValue: sourceKind) ?? .manual }
    var intent: UploadPostReadyIntent { UploadPostReadyIntent(rawValue: intentRaw) ?? .none }
}

/// Sendable-снимок, который можно передавать между SwiftData actor и
/// `URLSession`/MainActor. В нём нет upload-token.
struct UploadJobSnapshot: Sendable, Identifiable, Hashable {
    let id: String
    let sourceKind: String
    let sourceFilePath: String
    let fileName: String
    let mimeType: String
    let directoryID: String?
    let state: UploadJobState
    let bytesSent: Int64
    let totalBytes: Int64
    let sessionTaskIdentifier: Int
    let retries: Int
    let retryable: Bool
    let lastError: String?
    let createdAt: Date
    let updatedAt: Date
    let idempotencyKey: String
    let sessionID: String
    let fileID: String
    let sha256: String
    let partSize: Int64
    let currentPart: Int
    let intent: UploadPostReadyIntent
    let albumID: String?
    let localIdentifier: String?

    init(_ job: UploadJob) {
        self.id = job.id
        self.sourceKind = job.sourceKind
        self.sourceFilePath = job.sourceFilePath
        self.fileName = job.fileName
        self.mimeType = job.mimeType
        self.directoryID = job.directoryID
        self.fileID = job.fileID
        self.state = job.state
        self.bytesSent = job.bytesSent
        self.totalBytes = job.totalBytes
        self.sessionTaskIdentifier = job.sessionTaskIdentifier
        self.retries = job.retries
        self.retryable = job.retryable
        self.lastError = job.lastError
        self.createdAt = job.createdAt
        self.updatedAt = job.updatedAt
        self.idempotencyKey = job.idempotencyKey
        self.sessionID = job.sessionID
        self.sha256 = job.sha256
        self.partSize = job.partSize
        self.currentPart = job.currentPart
        self.intent = job.intent
        self.albumID = job.albumID
        self.localIdentifier = job.localIdentifier
    }

    var source: UploadJobSource { UploadJobSource(rawValue: sourceKind) ?? .manual }
}

enum UploadPostReadyResult: Sendable {
    case completed
    case failed(String)
}
