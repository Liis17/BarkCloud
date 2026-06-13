import Foundation
import GRPCCore

public enum FileTransferError: Error {
    case badURL
    case badUploadResponse
    case downloadFailed
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
}
