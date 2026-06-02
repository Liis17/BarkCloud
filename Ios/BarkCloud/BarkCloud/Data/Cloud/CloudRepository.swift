import Foundation
import GRPCCore
import SwiftProtobuf

/// Доступ к сервису Files: галерея (`ListUserMedia`), каталоги (`CloudApi`) и
/// загрузка файлов (через `FileTransferService`). Доменные ошибки пробрасываются
/// как `RPCError`; UI маппит их через `domainErrorMessage(_:)`.
final class CloudRepository: Sendable {
    private let grpc: GrpcManager
    let transfer: FileTransferService

    init(grpc: GrpcManager, transfer: FileTransferService) {
        self.grpc = grpc
        self.transfer = transfer
    }

    // MARK: - Галерея

    func listUserMedia(
        kind: CloudMediaKind,
        limit: Int32 = 50,
        cursorCreatedAt: Date? = nil,
        cursorFileID: String = ""
    ) async throws -> MediaPage {
        let stub = try await grpc.cloudStub()
        var req = Barkcloud_Files_ListUserMediaRequest()
        req.kind = (kind == .video) ? .video : .photo
        req.limit = limit
        if let cursorCreatedAt {
            req.cursorCreatedAt = Google_Protobuf_Timestamp(date: cursorCreatedAt)
        }
        req.cursorFileID = cursorFileID
        let resp = try await stub.listUserMedia(req)
        return MediaPage(
            items: resp.items.map { MediaAsset($0.file) },
            nextCursorCreatedAt: resp.hasNextCursorCreatedAt ? resp.nextCursorCreatedAt.date : nil,
            nextCursorFileID: resp.nextCursorFileID
        )
    }

    /// Удалить медиа из галереи по `file_id`. На сервере: живые записи каталога →
    /// в корзину (восстановимо); если записей нет — снять владельца (жёстко).
    func deleteUserMedia(fileID: String) async throws {
        let stub = try await grpc.cloudStub()
        var req = Barkcloud_Files_DeleteUserMediaRequest()
        req.fileID = fileID
        _ = try await stub.deleteUserMedia(req)
    }

    /// Пакетная проверка наличия файлов в облаке по SHA256-хешам (без побочных
    /// эффектов). Возвращает словарь нормализованный_хеш → есть ли в облаке.
    func checkFileHashes(_ hashes: [String]) async throws -> [String: Bool] {
        guard !hashes.isEmpty else { return [:] }
        let stub = try await grpc.filesStub()
        var req = Barkcloud_Files_CheckFileHashesRequest()
        req.fileHashes = hashes
        let resp = try await stub.checkFileHashes(req)
        var map: [String: Bool] = [:]
        for result in resp.results { map[result.fileHash] = result.exists }
        return map
    }

    /// Резолв `file_id` по одиночному SHA256-хешу. Возвращает `nil`, если файла
    /// нет в облаке. Используется галереей устройства, чтобы получить `file_id`
    /// ассета без полной перезаливки (а если его нет — заливаем отдельно).
    func checkFileHash(_ hash: String) async throws -> String? {
        let stub = try await grpc.filesStub()
        var req = Barkcloud_Files_CheckFileHashRequest()
        req.fileHash = hash
        let resp = try await stub.checkFileHash(req)
        return resp.fileID.isEmpty ? nil : resp.fileID
    }

    /// Расширенные метаданные блоба (EXIF / ffprobe / PDF / Office). `nil`, если
    /// сервер ещё не извлёк (или нечего извлекать) — экран свойств в этом случае
    /// показывает только базовые поля.
    func getFileMetadata(fileID: String) async throws -> CloudFileMetadata? {
        let stub = try await grpc.filesStub()
        var req = Barkcloud_Files_GetFileMetadataRequest()
        req.fileID = fileID
        let resp = try await stub.getFileMetadata(req)
        return resp.hasMetadata ? CloudFileMetadata(resp.metadata) : nil
    }

    // MARK: - Публичные ссылки

    /// Создать постоянную публичную share-ссылку на свой файл. URL собирается на
    /// клиенте из `token` (см. `GrpcEndpoint.publicShareURL`).
    func createShare(fileID: String, name: String) async throws -> ShareLink {
        let stub = try await grpc.cloudStub()
        var req = Barkcloud_Files_CreateShareRequest()
        req.fileID = fileID
        req.name = name
        return ShareLink(try await stub.createShare(req))
    }

    /// Список моих публичных ссылок (от свежих к старым). Курсорная пагинация:
    /// первый вызов — `cursorCreatedAt = nil`, далее передавать `nextCursor*`
    /// из ответа. `page.hasMore == false` — конец списка.
    func listMyShares(
        limit: Int = 60,
        cursorCreatedAt: Date? = nil,
        cursorShareID: String = ""
    ) async throws -> ShareLinksPage {
        let stub = try await grpc.cloudStub()
        var req = Barkcloud_Files_ListMySharesRequest()
        req.limit = Int32(max(1, min(200, limit)))
        if let cursorCreatedAt {
            req.cursorCreatedAt = Google_Protobuf_Timestamp(date: cursorCreatedAt)
        }
        if !cursorShareID.isEmpty {
            req.cursorShareID = cursorShareID
        }
        let resp = try await stub.listMyShares(req)
        return ShareLinksPage(
            items: resp.shares.map(ShareLink.init),
            nextCursorCreatedAt: resp.hasNextCursorCreatedAt ? resp.nextCursorCreatedAt.date : nil,
            nextCursorShareID: resp.nextCursorShareID
        )
    }

    /// Отозвать публичную ссылку. Идемпотентно: повторный вызов на уже
    /// отозванной ссылке проходит без ошибки. После — `/s/{token}` отдаёт 404.
    func revokeShare(id: String) async throws {
        let stub = try await grpc.cloudStub()
        var req = Barkcloud_Files_RevokeShareRequest()
        req.shareID = id
        _ = try await stub.revokeShare(req)
    }

    // MARK: - Шаринг с конкретным пользователем

    /// Выдать пользователю доступ к одному файлу. Идемпотентно: повторный
    /// `ShareFileWithUser` для уже расшаренного файла проходит без ошибки.
    func shareFileWithUser(fileID: String, recipientUserID: Int64) async throws {
        let stub = try await grpc.cloudStub()
        var req = Barkcloud_Files_ShareFileWithUserRequest()
        req.fileID = fileID
        req.recipientUserID = recipientUserID
        _ = try await stub.shareFileWithUser(req)
    }

    /// Файлы, которыми со мной поделились другие пользователи. От свежих к
    /// старым. Курсорная пагинация: `cursorSharedAt = nil` — первая страница.
    func listSharedWithMe(
        limit: Int = 60,
        cursorSharedAt: Date? = nil,
        cursorGrantID: String = ""
    ) async throws -> SharedWithMePage {
        let stub = try await grpc.cloudStub()
        var req = Barkcloud_Files_ListSharedWithMeRequest()
        req.limit = Int32(max(1, min(200, limit)))
        if let cursorSharedAt {
            req.cursorSharedAt = Google_Protobuf_Timestamp(date: cursorSharedAt)
        }
        if !cursorGrantID.isEmpty {
            req.cursorGrantID = cursorGrantID
        }
        let resp = try await stub.listSharedWithMe(req)
        return SharedWithMePage(
            items: resp.items.map(SharedFileEntry.init),
            nextCursorSharedAt: resp.hasNextCursorSharedAt ? resp.nextCursorSharedAt.date : nil,
            nextCursorGrantID: resp.nextCursorGrantID
        )
    }

    /// Временный публичный URL для скачивания файла, которым со мной поделились
    /// (grant-проверка идёт на бэкенде). URL живёт `TempFiles:ExpiresAt` минут,
    /// потом 404 → надо запросить заново.
    func getSharedFileDownloadUrl(fileID: String) async throws -> URL? {
        let stub = try await grpc.cloudStub()
        var req = Barkcloud_Files_GetSharedFileDownloadUrlRequest()
        req.fileID = fileID
        let resp = try await stub.getSharedFileDownloadUrl(req)
        return URL(string: resp.downloadURL)
    }

    /// Список активных грантов на один файл — кому я сейчас раздал доступ.
    /// Сортировка: от свежего к старому (как на бэке).
    func listMyOutgoingShares(fileID: String) async throws -> [OutgoingShare] {
        let stub = try await grpc.cloudStub()
        var req = Barkcloud_Files_ListMyOutgoingSharesRequest()
        req.fileID = fileID
        let resp = try await stub.listMyOutgoingShares(req)
        return resp.items.map(OutgoingShare.init)
    }

    /// Отозвать грант — у получателя файл сразу пропадёт из «Мне доступны»,
    /// `getSharedFileDownloadUrl` для него начнёт отдавать ошибку. Идемпотентно:
    /// повторный revoke на уже отозванном проходит без исключения.
    func revokeUserShare(grantID: String) async throws {
        let stub = try await grpc.cloudStub()
        var req = Barkcloud_Files_RevokeUserShareRequest()
        req.grantID = grantID
        _ = try await stub.revokeUserShare(req)
    }

    /// Все мои исходящие гранты — файлы, которыми я поделился с пользователями
    /// (плоский список грант-за-грантом, от свежих к старым). Группировку по
    /// файлу для таба «Я поделился» делает вызывающий VM. Курсорная пагинация:
    /// `cursorSharedAt = nil` — первая страница.
    func listMyOutgoingSharesAll(
        limit: Int = 60,
        cursorSharedAt: Date? = nil,
        cursorGrantID: String = ""
    ) async throws -> OutgoingSharesAllPage {
        let stub = try await grpc.cloudStub()
        var req = Barkcloud_Files_ListMyOutgoingSharesAllRequest()
        req.limit = Int32(max(1, min(200, limit)))
        if let cursorSharedAt {
            req.cursorSharedAt = Google_Protobuf_Timestamp(date: cursorSharedAt)
        }
        if !cursorGrantID.isEmpty {
            req.cursorGrantID = cursorGrantID
        }
        let resp = try await stub.listMyOutgoingSharesAll(req)
        return OutgoingSharesAllPage(
            items: resp.items.map(OutgoingShareFull.init),
            nextCursorSharedAt: resp.hasNextCursorSharedAt ? resp.nextCursorSharedAt.date : nil,
            nextCursorGrantID: resp.nextCursorGrantID
        )
    }

    // MARK: - Каталоги

    /// Содержимое папки с полной информацией о файлах (превью/размеры). `""` = корень.
    func listDirectory(_ directoryID: String) async throws -> CloudListing {
        let stub = try await grpc.cloudStub()
        var req = Barkcloud_Files_ListDirectoryRequest()
        req.directoryID = directoryID
        let resp = try await stub.listDirectoryDetailed(req)
        return CloudListing(
            subdirs: resp.subdirs.map(CloudDirectory.init),
            files: resp.files.map(CloudFileEntry.init)
        )
    }

    /// Хлебные крошки до папки (от корня, не включая саму папку).
    func path(directoryID: String) async throws -> [PathCrumb] {
        let stub = try await grpc.cloudStub()
        var req = Barkcloud_Files_GetPathRequest()
        req.target = .directoryID(directoryID)
        let resp = try await stub.getPath(req)
        return resp.segments.map { PathCrumb(id: $0.id, name: $0.name) }
    }

    @discardableResult
    func createDirectory(parentID: String, name: String) async throws -> CloudDirectory {
        let stub = try await grpc.cloudStub()
        var req = Barkcloud_Files_CreateDirectoryRequest()
        req.parentID = parentID
        req.name = name
        return CloudDirectory(try await stub.createDirectory(req))
    }

    func renameDirectory(_ directoryID: String, newName: String) async throws {
        let stub = try await grpc.cloudStub()
        var req = Barkcloud_Files_RenameDirectoryRequest()
        req.directoryID = directoryID
        req.newName = newName
        _ = try await stub.renameDirectory(req)
    }

    func moveDirectory(_ directoryID: String, newParentID: String) async throws {
        let stub = try await grpc.cloudStub()
        var req = Barkcloud_Files_MoveDirectoryRequest()
        req.directoryID = directoryID
        req.newParentID = newParentID
        _ = try await stub.moveDirectory(req)
    }

    func deleteDirectory(_ directoryID: String) async throws {
        let stub = try await grpc.cloudStub()
        var req = Barkcloud_Files_DeleteDirectoryRequest()
        req.directoryID = directoryID
        _ = try await stub.deleteDirectory(req)
    }

    // MARK: - Записи о файлах

    /// Привязать блоб к каталогу. Если `routeByMediaKind == true` — сервер
    /// игнорирует `directoryID` и кладёт файл в системную папку «Фото»/«Видео»/
    /// «Другие документы» по типу медиа.
    func attachFile(
        fileID: String,
        directoryID: String,
        name: String,
        routeByMediaKind: Bool = false
    ) async throws {
        let stub = try await grpc.cloudStub()
        var req = Barkcloud_Files_AttachFileRequest()
        req.fileID = fileID
        req.directoryID = directoryID
        req.name = name
        req.routeByMediaKind = routeByMediaKind
        _ = try await stub.attachFile(req)
    }

    func renameFileEntry(_ entryID: String, newName: String) async throws {
        let stub = try await grpc.cloudStub()
        var req = Barkcloud_Files_RenameFileEntryRequest()
        req.entryID = entryID
        req.newName = newName
        _ = try await stub.renameFileEntry(req)
    }

    func moveFileEntry(_ entryID: String, newDirectoryID: String) async throws {
        let stub = try await grpc.cloudStub()
        var req = Barkcloud_Files_MoveFileEntryRequest()
        req.entryID = entryID
        req.newDirectoryID = newDirectoryID
        _ = try await stub.moveFileEntry(req)
    }

    func deleteFileEntry(_ entryID: String) async throws {
        let stub = try await grpc.cloudStub()
        var req = Barkcloud_Files_DeleteFileEntryRequest()
        req.entryID = entryID
        _ = try await stub.deleteFileEntry(req)
    }

    // MARK: - Корзина

    /// Список файлов в корзине (от свежеудалённых к старым), cursor-пагинация.
    func listTrash(
        limit: Int32 = 50,
        cursorDeletedAt: Date? = nil,
        cursorEntryID: String = ""
    ) async throws -> TrashPage {
        let stub = try await grpc.cloudStub()
        var req = Barkcloud_Files_ListTrashRequest()
        req.limit = limit
        if let cursorDeletedAt {
            req.cursorDeletedAt = Google_Protobuf_Timestamp(date: cursorDeletedAt)
        }
        req.cursorEntryID = cursorEntryID
        let resp = try await stub.listTrash(req)
        return TrashPage(
            items: resp.items.map(TrashItem.init),
            nextCursorDeletedAt: resp.hasNextCursorDeletedAt ? resp.nextCursorDeletedAt.date : nil,
            nextCursorEntryID: resp.nextCursorEntryID
        )
    }

    func restoreFromTrash(entryID: String) async throws {
        let stub = try await grpc.cloudStub()
        var req = Barkcloud_Files_RestoreFromTrashRequest()
        req.entryID = entryID
        _ = try await stub.restoreFromTrash(req)
    }

    func deleteFromTrash(entryID: String) async throws {
        let stub = try await grpc.cloudStub()
        var req = Barkcloud_Files_DeleteFromTrashRequest()
        req.entryID = entryID
        _ = try await stub.deleteFromTrash(req)
    }

    func emptyTrash() async throws {
        let stub = try await grpc.cloudStub()
        _ = try await stub.emptyTrash(Barkcloud_Files_EmptyTrashRequest())
    }

    // MARK: - Загрузка

    /// Загрузить файл в облако. Если `routeByMediaKind == true` — сервер сам
    /// разложит файл по системным папкам «Фото»/«Видео»/«Другие документы» по типу
    /// медиа (явный `directoryID` игнорируется). Иначе при заданном `directoryID` —
    /// привязать к этой папке. Возвращает `file_id` блоба.
    @discardableResult
    func uploadFile(
        data: Data,
        fileName: String,
        toDirectory directoryID: String? = nil,
        routeByMediaKind: Bool = false
    ) async throws -> String {
        let upload = try await transfer.getUploadURL(type: .cloudFile)
        let fileID = try await transfer.upload(data: data, fileName: fileName, to: upload.url)
        if routeByMediaKind {
            try await attachFile(fileID: fileID, directoryID: "", name: fileName, routeByMediaKind: true)
        } else if let directoryID {
            try await attachFile(fileID: fileID, directoryID: directoryID, name: fileName)
        }
        return fileID
    }

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
        let totalBytes = try MultipartBodyBuilder.writeMultipartFile(
            boundary: UploadConstants.multipartBoundary,
            fileName: fileName,
            mimeType: mime,
            sourceFile: sourceFile,
            destination: multipartURL
        )
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
        return try await enqueueBackgroundUpload(
            sourceFile: tempURL,
            fileName: fileName,
            mimeType: mimeType,
            toDirectory: directoryID,
            source: source
        )
    }
}
