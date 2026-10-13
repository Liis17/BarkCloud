import Foundation
import GRPCCore

public enum FileTransferError: Error, LocalizedError {
    case badURL
    case badUploadResponse
    case badPartAcknowledgement
    case downloadFailed
    case emptyFile
    case avatarTooLarge
    case stagingFailed
    case sourceChangedWhileReading
    case invalidSessionResponse(String)
    case recoveryLimitReached
    case processingTimedOut
    case server(errorCode: String?, message: String?)
    case http(status: Int, errorCode: String?, message: String?)

    public var errorDescription: String? {
        switch self {
        case .badURL: return "Некорректный адрес файла"
        case .badUploadResponse: return "Сервер вернул некорректный ответ загрузки"
        case .badPartAcknowledgement: return "Сервер подтвердил неверный номер или размер части"
        case .downloadFailed: return "Не удалось скачать файл"
        case .emptyFile: return "Нельзя загрузить пустой файл"
        case .avatarTooLarge: return "Размер аватара превышает 20 МиБ"
        case .stagingFailed: return "Не удалось подготовить временный файл для загрузки"
        case .sourceChangedWhileReading: return "Исходный файл изменился во время загрузки"
        case .invalidSessionResponse(let message): return message
        case .recoveryLimitReached: return "Не удалось восстановить загрузку после нескольких попыток"
        case .processingTimedOut: return "Сервер не обработал аватар за 120 секунд"
        case .server(_, let message): return message ?? "Ошибка upload-сессии"
        case .http(let status, _, let message): return message ?? "Ошибка HTTP загрузки (\(status))"
        }
    }

    public var serverErrorCode: String? {
        switch self {
        case .server(let errorCode, _), .http(_, let errorCode, _): return errorCode
        default: return nil
        }
    }
}

/// Состояние серверной resumable upload-сессии. Токен намеренно не входит в
/// persisted-модели клиентов: он живёт только в памяти текущего процесса и
/// заново выдаётся через `resumeUploadSession` после перезапуска.
public enum FileUploadSessionStatus: Sendable, Equatable {
    case uploading
    case processing
    case ready
    case failed
    case cancelled
    case expired
    case unspecified
}

public struct FileUploadPart: Sendable, Equatable {
    public let partNumber: Int
    public let size: Int64
    public let hasETag: Bool

    public init(partNumber: Int, size: Int64, hasETag: Bool) {
        self.partNumber = partNumber
        self.size = size
        self.hasETag = hasETag
    }
}

public struct FileUploadSession: Sendable, Equatable {
    public let sessionID: String
    public let fileID: String
    public let status: FileUploadSessionStatus
    public let fileSize: Int64
    public let partSize: Int64
    public let expiresAt: Date?
    public let uploadToken: String?
    public let uploadedParts: [FileUploadPart]
    public let errorCode: String?
    public let errorMessage: String?

    public init(
        sessionID: String,
        fileID: String,
        status: FileUploadSessionStatus,
        fileSize: Int64,
        partSize: Int64,
        expiresAt: Date?,
        uploadToken: String?,
        uploadedParts: [FileUploadPart],
        errorCode: String?,
        errorMessage: String?
    ) {
        self.sessionID = sessionID
        self.fileID = fileID
        self.status = status
        self.fileSize = fileSize
        self.partSize = partSize
        self.expiresAt = expiresAt
        self.uploadToken = uploadToken
        self.uploadedParts = uploadedParts
        self.errorCode = errorCode
        self.errorMessage = errorMessage
    }
}

/// Передача файлов через общий Upload 2.0 и HTTP-скачивание через `InsecureHTTP`.
public final class FileTransferService: Sendable {
    private let grpc: GrpcManager
    private let uploadTransport: any UploadSessionTransport
    private let uploader: Upload2Uploader

    public init(grpc: GrpcManager) {
        self.grpc = grpc
        let transport = GrpcUploadSessionTransport(grpc: grpc)
        self.uploadTransport = transport
        self.uploader = Upload2Uploader(transport: transport)
    }

    init(grpc: GrpcManager, uploadTransport: any UploadSessionTransport, clock: any UploadClock) {
        self.grpc = grpc
        self.uploadTransport = uploadTransport
        self.uploader = Upload2Uploader(transport: uploadTransport, clock: clock)
    }

    public static func isCancellationError(_ error: Error) -> Bool {
        Upload2Uploader.isCancellation(error)
    }

    // MARK: - gRPC (FilesApi)

    // MARK: - Upload 2.0 control plane

    public func createUploadSession(
        idempotencyKey: String,
        fileName: String,
        fileSize: Int64,
        contentType: String,
        sha256: String,
        fileType: Barkcloud_Files_UploadFileType = .cloudFile,
        timeout: Duration? = nil
    ) async throws -> FileUploadSession {
        try await uploadTransport.create(
            idempotencyKey: idempotencyKey,
            fileName: fileName,
            fileSize: fileSize,
            contentType: contentType,
            sha256: sha256,
            fileType: fileType,
            timeout: timeout
        )
    }

    public func getUploadSession(sessionID: String, timeout: Duration? = nil) async throws -> FileUploadSession {
        try await uploadTransport.get(sessionID: sessionID, timeout: timeout)
    }

    public func resumeUploadSession(sessionID: String, timeout: Duration? = nil) async throws -> FileUploadSession {
        try await uploadTransport.resume(sessionID: sessionID, timeout: timeout)
    }

    public func completeUploadSession(sessionID: String, timeout: Duration? = nil) async throws -> FileUploadSession {
        try await uploadTransport.complete(sessionID: sessionID, timeout: timeout)
    }

    public func cancelUploadSession(sessionID: String, timeout: Duration? = nil) async throws -> FileUploadSession {
        try await uploadTransport.cancel(sessionID: sessionID, timeout: timeout)
    }

    /// HTTP/1 data-plane URL. nginx exposes this route on the public 443 host;
    /// it must not use the legacy `:7025/web` endpoint.
    public func uploadSessionPartURL(sessionID: String, partNumber: Int) throws -> URL {
        guard !sessionID.isEmpty, partNumber > 0,
              let url = URL(string: "\(GrpcEndpoint.webHost)/file-upload/\(sessionID)/parts/\(partNumber)")
        else { throw FileTransferError.badURL }
        return url
    }

    /// Временные ссылки на оригиналы по file_id (file_id → URL).
    public func tempDownloadURLs(fileIDs: [String]) async throws -> [String: URL] {
        guard !fileIDs.isEmpty else { return [:] }
        let stub = try await grpc.filesStub()
        var req = Barkcloud_Files_GetTempDownloadUrlRequest()
        req.fileIds = fileIDs
        let resp = try await stub.getTempDownloadUrl(req)
        var result: [String: URL] = [:]
        for item in resp.fileUrls where !item.url.isEmpty {
            result[item.fileID] = URL(string: item.url)
        }
        return result
    }

    /// Информация о хранилище пользователя (всё в байтах): использовано / лимит,
    /// а также разбивка физического диска сервера — всего, занято не-S3 данными,
    /// занято S3 (облаком).
    public func storageInfo() async throws -> (used: Int64, limit: Int64, diskTotal: Int64, diskOther: Int64, diskS3: Int64) {
        let stub = try await grpc.filesStub()
        let resp = try await stub.getUserStorageInfo(Barkcloud_Files_GetUserStorageInfoRequest())
        return (resp.totalUsedStorage, resp.storageLimit, resp.totalAvailableStorage, resp.diskUsedStorage, resp.s3UsedStorage)
    }

    /// Свежий access-токен (через проактивный refresh) — для использования в
    /// `BackgroundUploadCoordinator` при сборке `URLRequest`.
    public func validAccessToken() async -> String? {
        await grpc.validAccessToken()
    }

    // MARK: - Upload 2.0

    public func uploadFile(
        fileURL: URL,
        fileName: String? = nil,
        contentType: String = "application/octet-stream",
        fileType: Barkcloud_Files_UploadFileType = .cloudFile
    ) async throws -> FileUploadSession {
        try await uploader.upload(
            fileURL: fileURL,
            fileName: fileName ?? fileURL.lastPathComponent,
            contentType: contentType,
            fileType: fileType
        )
    }

    public func uploadFile(
        data: Data,
        fileName: String,
        contentType: String = "application/octet-stream",
        fileType: Barkcloud_Files_UploadFileType = .cloudFile
    ) async throws -> FileUploadSession {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("BarkCloudStage-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let fileURL = directory.appendingPathComponent("payload")
        guard FileManager.default.createFile(atPath: fileURL.path, contents: nil) else {
            throw FileTransferError.stagingFailed
        }
        let handle = try FileHandle(forWritingTo: fileURL)
        defer { try? handle.close() }
        try handle.write(contentsOf: data)
        try handle.close()
        return try await uploadFile(
            fileURL: fileURL,
            fileName: fileName,
            contentType: contentType,
            fileType: fileType
        )
    }

    /// Скачать оригинал во временный файл (для предпросмотра / шеринга).
    /// Каждое скачивание кладётся в собственную UUID-поддиректорию: параллельные
    /// загрузки файлов с одинаковым именем иначе гонялись бы за один путь
    /// (removeItem выдёргивал бы файл из-под читателя).
    public func download(from url: URL, suggestedName: String) async throws -> URL {
        let (tempURL, response) = try await InsecureHTTP.session.download(from: url)
        defer { try? FileManager.default.removeItem(at: tempURL) }
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            throw FileTransferError.downloadFailed
        }
        let safeName = suggestedName.replacingOccurrences(of: "/", with: ":")
        let name = safeName.isEmpty ? UUID().uuidString : safeName
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let dest = dir.appendingPathComponent(name)
        try FileManager.default.moveItem(at: tempURL, to: dest)
        return dest
    }

    static func mapUploadSession(_ response: Barkcloud_Files_UploadSessionResponse) -> FileUploadSession {
        let status: FileUploadSessionStatus
        switch response.status {
        case .uploading: status = .uploading
        case .processing: status = .processing
        case .ready: status = .ready
        case .failed: status = .failed
        case .cancelled: status = .cancelled
        case .expired: status = .expired
        default: status = .unspecified
        }
        return FileUploadSession(
            sessionID: response.sessionID,
            fileID: response.fileID,
            status: status,
            fileSize: response.fileSize,
            partSize: response.partSize,
            expiresAt: response.hasExpiresAt ? response.expiresAt.date : nil,
            uploadToken: response.uploadToken.isEmpty ? nil : response.uploadToken,
            uploadedParts: response.uploadedParts.map {
                FileUploadPart(partNumber: Int($0.partNumber), size: $0.size, hasETag: $0.hasEtag_p)
            },
            errorCode: response.errorCode.isEmpty ? nil : response.errorCode,
            errorMessage: response.errorMessage.isEmpty ? nil : response.errorMessage
        )
    }
}
