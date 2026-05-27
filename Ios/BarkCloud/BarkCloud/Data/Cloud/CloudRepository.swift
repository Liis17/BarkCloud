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

    func attachFile(fileID: String, directoryID: String, name: String) async throws {
        let stub = try await grpc.cloudStub()
        var req = Barkcloud_Files_AttachFileRequest()
        req.fileID = fileID
        req.directoryID = directoryID
        req.name = name
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

    /// Загрузить файл в облако. Если задан `directoryID` — привязать к папке.
    /// Возвращает `file_id` блоба (из ответа сервера; учитывает дедупликацию).
    @discardableResult
    func uploadFile(data: Data, fileName: String, toDirectory directoryID: String? = nil) async throws -> String {
        let upload = try await transfer.getUploadURL(type: .cloudFile)
        let fileID = try await transfer.upload(data: data, fileName: fileName, to: upload.url)
        if let directoryID {
            try await attachFile(fileID: fileID, directoryID: directoryID, name: fileName)
        }
        return fileID
    }
}
