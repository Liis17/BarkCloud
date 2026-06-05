import Foundation
import GRPCCore
import SwiftProtobuf

/// Доступ к сервису умных (динамических) папок (`DynamicFolderApi`). Зеркалит
/// `AlbumRepository`: доменные ошибки пробрасываются как `RPCError`, UI маппит их
/// через `domainErrorMessage(_:)`. Системные папки приходят в списке первыми.
public final class DynamicFolderRepository: Sendable {
    private let grpc: GrpcManager

    public init(grpc: GrpcManager) { self.grpc = grpc }

    /// Системные + пользовательские папки (с обложкой/счётчиком).
    public func listFolders() async throws -> [DynamicFolderCard] {
        let stub = try await grpc.dynamicFolderStub()
        let resp = try await stub.listDynamicFolders(Barkcloud_Files_ListDynamicFoldersRequest())
        return resp.folders.map(DynamicFolderCard.init)
    }

    /// Содержимое папки по критериям (cursor-пагинация, опциональный фильтр по типу).
    public func listItems(
        folderID: String,
        kindFilter: CloudMediaKind? = nil,
        limit: Int32 = 50,
        cursorCreatedAt: Date? = nil,
        cursorFileID: String = ""
    ) async throws -> DynamicFolderItemsPage {
        let stub = try await grpc.dynamicFolderStub()
        var req = Barkcloud_Files_ListDynamicFolderItemsRequest()
        req.folderID = folderID
        req.limit = limit
        if let cursorCreatedAt {
            req.cursorCreatedAt = Google_Protobuf_Timestamp(date: cursorCreatedAt)
        }
        req.cursorFileID = cursorFileID
        if kindFilter == .video {
            req.kindFilter = .video
        } else if kindFilter == .photo {
            req.kindFilter = .photo
        }
        let resp = try await stub.listDynamicFolderItems(req)
        return DynamicFolderItemsPage(
            items: resp.items.map { MediaAsset($0.file) },
            nextCursorCreatedAt: resp.hasNextCursorCreatedAt ? resp.nextCursorCreatedAt.date : nil,
            nextCursorFileID: resp.nextCursorFileID
        )
    }

    @discardableResult
    public func create(
        name: String,
        combinator: Barkcloud_Files_DfCombinator,
        rules: [DynamicFolderRule],
        viewMode: Barkcloud_Files_DfViewMode
    ) async throws -> DynamicFolderCard {
        let stub = try await grpc.dynamicFolderStub()
        var req = Barkcloud_Files_CreateDynamicFolderRequest()
        req.name = name
        req.combinator = combinator
        req.rules = rules.map(\.proto)
        req.viewMode = viewMode
        return DynamicFolderCard(try await stub.createDynamicFolder(req))
    }

    /// Обновление: `name`/`viewMode` — optional-поля (заданы = «менять»); правила и
    /// комбинатор всегда заменяются целиком (как в вебе).
    @discardableResult
    public func update(
        folderID: String,
        name: String,
        combinator: Barkcloud_Files_DfCombinator,
        rules: [DynamicFolderRule],
        viewMode: Barkcloud_Files_DfViewMode
    ) async throws -> DynamicFolderCard {
        let stub = try await grpc.dynamicFolderStub()
        var req = Barkcloud_Files_UpdateDynamicFolderRequest()
        req.folderID = folderID
        req.name = name
        req.combinator = combinator
        req.rules = rules.map(\.proto)
        req.viewMode = viewMode
        return DynamicFolderCard(try await stub.updateDynamicFolder(req))
    }

    public func delete(folderID: String) async throws {
        let stub = try await grpc.dynamicFolderStub()
        var req = Barkcloud_Files_DeleteDynamicFolderRequest()
        req.folderID = folderID
        _ = try await stub.deleteDynamicFolder(req)
    }
}
