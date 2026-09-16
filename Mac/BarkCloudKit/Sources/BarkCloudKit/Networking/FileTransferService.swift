import Foundation
import GRPCCore

public enum FileTransferError: Error {
    case badURL
    case badUploadResponse
    case downloadFailed
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

/// Передача байтов файлов: gRPC `FilesApi` (получение ссылок/квоты) + обычный HTTP
/// upload/download на готовые URL, которые возвращает сервер. Загрузка/скачивание
/// идут НЕ через gRPC, а POST/GET на `:7025/web/upload|download/{id}` через
/// `InsecureHTTP.session` (self-signed TLS).
public final class FileTransferService: Sendable {
    private let grpc: GrpcManager

    public init(grpc: GrpcManager) {
        self.grpc = grpc
    }

    // MARK: - gRPC (FilesApi)

    /// Получить адрес для загрузки и предварительный file_id.
    public func getUploadURL(type: Barkcloud_Files_UploadFileType) async throws -> (url: String, fileID: String) {
        let stub = try await grpc.filesStub()
        var req = Barkcloud_Files_GetUploadUrlRequest()
        req.fileType = type
        let resp = try await stub.getUploadUrl(req)
        return (resp.url, resp.fileID)
    }

    // MARK: - Upload 2.0 control plane

    public func createUploadSession(
        idempotencyKey: String,
        fileName: String,
        fileSize: Int64,
        contentType: String,
        sha256: String
    ) async throws -> FileUploadSession {
        let stub = try await grpc.filesStub()
        var req = Barkcloud_Files_CreateUploadSessionRequest()
        req.idempotencyKey = idempotencyKey
        req.fileName = fileName
        req.fileSize = fileSize
        req.contentType = contentType
        req.sha256 = sha256
        return Self.mapUploadSession(try await stub.createUploadSession(req))
    }

    public func getUploadSession(sessionID: String) async throws -> FileUploadSession {
        let stub = try await grpc.filesStub()
        var req = Barkcloud_Files_UploadSessionIdRequest()
        req.sessionID = sessionID
        return Self.mapUploadSession(try await stub.getUploadSession(req))
    }

    public func resumeUploadSession(sessionID: String) async throws -> FileUploadSession {
        let stub = try await grpc.filesStub()
        var req = Barkcloud_Files_UploadSessionIdRequest()
        req.sessionID = sessionID
        return Self.mapUploadSession(try await stub.resumeUploadSession(req))
    }

    public func completeUploadSession(sessionID: String) async throws -> FileUploadSession {
        let stub = try await grpc.filesStub()
        var req = Barkcloud_Files_UploadSessionIdRequest()
        req.sessionID = sessionID
        return Self.mapUploadSession(try await stub.completeUploadSession(req))
    }

    public func cancelUploadSession(sessionID: String) async throws -> FileUploadSession {
        let stub = try await grpc.filesStub()
        var req = Barkcloud_Files_UploadSessionIdRequest()
        req.sessionID = sessionID
        return Self.mapUploadSession(try await stub.cancelUploadSession(req))
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

    // MARK: - HTTP

    /// Залить байты по адресу из `getUploadURL`. Возвращает `fileId` ИЗ ОТВЕТА —
    /// при дедупликации он может отличаться от запрошенного, всегда используем его.
    public func upload(data: Data, fileName: String, to urlString: String) async throws -> String {
        guard let url = URL(string: urlString) else { throw FileTransferError.badURL }

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        if let token = await grpc.validAccessToken(), !token.isEmpty {
            request.setValue(token, forHTTPHeaderField: "x-auth-token")
        }
        let boundary = "Boundary-\(UUID().uuidString)"
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        let body = Self.multipartBody(boundary: boundary, fieldName: "file", fileName: fileName, data: data)

        let (respData, response) = try await InsecureHTTP.session.upload(for: request, from: body)
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            throw FileTransferError.badUploadResponse
        }
        guard let obj = try? JSONSerialization.jsonObject(with: respData) as? [String: Any],
              let fileID = obj["fileId"] as? String, !fileID.isEmpty else {
            throw FileTransferError.badUploadResponse
        }
        return fileID
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

    private static func multipartBody(boundary: String, fieldName: String, fileName: String, data: Data) -> Data {
        var body = Data()
        body.append("--\(boundary)\r\n".data(using: .utf8)!)
        body.append("Content-Disposition: form-data; name=\"\(fieldName)\"; filename=\"\(fileName)\"\r\n".data(using: .utf8)!)
        body.append("Content-Type: application/octet-stream\r\n\r\n".data(using: .utf8)!)
        body.append(data)
        body.append("\r\n--\(boundary)--\r\n".data(using: .utf8)!)
        return body
    }

    private static func mapUploadSession(_ response: Barkcloud_Files_UploadSessionResponse) -> FileUploadSession {
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
