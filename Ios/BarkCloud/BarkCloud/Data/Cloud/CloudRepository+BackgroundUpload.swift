import Foundation
import BarkCloudKit

// Фоновая загрузка — iOS-only слой поверх общего `CloudRepository` (BarkCloudKit).
// Зависит от App Group очереди (`UploadQueueStore`), BGTask-координатора
// (`BackgroundUploadCoordinator`) и констант (`UploadConstants`), которые остаются
// в iOS-таргете. Поэтому живёт в приложении, а не в пакете.
extension CloudRepository {
    /// Поставить файл в фоновую очередь (`BackgroundUploadCoordinator`).
    /// 1. Получаем uploadURL через gRPC.
    /// 2. Готовим multipart body файл в App Group container (стримом, не в RAM).
    /// 3. Записываем UploadJob и submit'им в координатор.
    /// Привязка к папке (`AttachFile`) случится автоматически после успешного
    /// завершения — в `AppEnvironment` подписана на `onJobCompleted`.
    ///
    /// Возвращает id UploadJob — UI может следить за прогрессом через координатор.
    @discardableResult
    func enqueueBackgroundUpload(
        sourceFile: URL,
        fileName: String,
        mimeType: String? = nil,
        toDirectory directoryID: String? = nil,
        source: UploadJobSource = .manual
    ) async throws -> String {
        let upload = try await transfer.getUploadURL(type: .cloudFile)
        guard let stagingDir = UploadConstants.stagingDirectory else {
            throw FileTransferError.badURL
        }
        let multipartURL = stagingDir.appendingPathComponent("\(UUID().uuidString).body")
        let mime = mimeType ?? MimeIcon.mime(forFileName: fileName)
        let totalBytes: Int64
        do {
            totalBytes = try MultipartBodyBuilder.writeMultipartFile(
                boundary: UploadConstants.multipartBoundary,
                fileName: fileName,
                mimeType: mime,
                sourceFile: sourceFile,
                destination: multipartURL
            )
        } catch {
            try? FileManager.default.removeItem(at: multipartURL)
            throw error
        }
        let snapshot = await UploadQueueStore.shared.create(
            sourceKind: source,
            sourceFilePath: sourceFile.path,
            multipartBodyPath: multipartURL.path,
            fileName: fileName,
            mimeType: mime,
            directoryID: directoryID,
            uploadURL: upload.url,
            preparedFileID: upload.fileID,
            totalBytes: totalBytes
        )
        await BackgroundUploadCoordinator.shared.submit(jobID: snapshot.id)
        return snapshot.id
    }

    /// Удобная обёртка над `enqueueBackgroundUpload(sourceFile:)`: для случаев,
    /// когда у вызывающего кода есть Data (PhotosPicker, fileImporter). Пишет во
    /// временный файл в App Group, потом ставит обычный фоновый job.
    @discardableResult
    func enqueueBackgroundUpload(
        data: Data,
        fileName: String,
        mimeType: String? = nil,
        toDirectory directoryID: String? = nil,
        source: UploadJobSource = .manual
    ) async throws -> String {
        guard let stagingDir = UploadConstants.stagingDirectory else {
            throw FileTransferError.badURL
        }
        let tempURL = stagingDir.appendingPathComponent("\(UUID().uuidString)-\(fileName)")
        try data.write(to: tempURL)
        do {
            return try await enqueueBackgroundUpload(
                sourceFile: tempURL,
                fileName: fileName,
                mimeType: mimeType,
                toDirectory: directoryID,
                source: source
            )
        } catch {
            try? FileManager.default.removeItem(at: tempURL)
            throw error
        }
    }
}
