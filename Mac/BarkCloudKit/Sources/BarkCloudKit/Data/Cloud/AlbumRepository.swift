import Foundation
import GRPCCore
import SwiftProtobuf

/// Доступ к сервису альбомов (`AlbumApi`). Доменные ошибки пробрасываются как
/// `RPCError`; UI маппит их через `domainErrorMessage(_:)`.
public final class AlbumRepository: Sendable {
    private let grpc: GrpcManager

    public init(grpc: GrpcManager) { self.grpc = grpc }

    public func listAlbums(
        limit: Int32 = 50,
        cursorUpdatedAt: Date? = nil,
        cursorAlbumID: String = ""
    ) async throws -> AlbumPage {
        let stub = try await grpc.albumStub()
        var req = Barkcloud_Files_ListAlbumsRequest()
        req.limit = limit
        if let cursorUpdatedAt {
            req.cursorUpdatedAt = Google_Protobuf_Timestamp(date: cursorUpdatedAt)
        }
        req.cursorAlbumID = cursorAlbumID
        let resp = try await stub.listAlbums(req)
        return AlbumPage(
            albums: resp.albums.map(AlbumCard.init),
            nextCursorUpdatedAt: resp.hasNextCursorUpdatedAt ? resp.nextCursorUpdatedAt.date : nil,
            nextCursorAlbumID: resp.nextCursorAlbumID
        )
    }

    /// Элементы альбома (с опциональным фильтром по типу).
    public func listItems(
        albumID: String,
        kindFilter: CloudMediaKind? = nil,
        limit: Int32 = 50,
        cursorAddedAt: Date? = nil,
        cursorFileID: String = ""
    ) async throws -> (items: [MediaAsset], nextAddedAt: Date?, nextFileID: String) {
        let stub = try await grpc.albumStub()
        var req = Barkcloud_Files_ListAlbumItemsRequest()
        req.albumID = albumID
        req.limit = limit
        if let cursorAddedAt {
            req.cursorAddedAt = Google_Protobuf_Timestamp(date: cursorAddedAt)
        }
        req.cursorFileID = cursorFileID
        if kindFilter == .video {
            req.kindFilter = .video
        } else if kindFilter == .photo {
            req.kindFilter = .photo
        }
        let resp = try await stub.listAlbumItems(req)
        return (
            resp.items.map { MediaAsset($0.file) },
            resp.hasNextCursorAddedAt ? resp.nextCursorAddedAt.date : nil,
            resp.nextCursorFileID
        )
    }

    @discardableResult
    public func createAlbum(name: String, description: String = "") async throws -> AlbumCard {
        let stub = try await grpc.albumStub()
        var req = Barkcloud_Files_CreateAlbumRequest()
        req.name = name
        req.description_p = description
        return AlbumCard(try await stub.createAlbum(req))
    }

    @discardableResult
    public func updateAlbum(
        albumID: String,
        name: String? = nil,
        description: String? = nil,
        coverFileID: String? = nil
    ) async throws -> AlbumCard {
        let stub = try await grpc.albumStub()
        var req = Barkcloud_Files_UpdateAlbumRequest()
        req.albumID = albumID
        if let name { req.name = name }
        if let description { req.description_p = description }
        if let coverFileID { req.coverFileID = coverFileID }
        return AlbumCard(try await stub.updateAlbum(req))
    }

    public func deleteAlbum(_ albumID: String) async throws {
        let stub = try await grpc.albumStub()
        var req = Barkcloud_Files_DeleteAlbumRequest()
        req.albumID = albumID
        _ = try await stub.deleteAlbum(req)
    }

    public func addItems(albumID: String, fileIDs: [String]) async throws {
        let stub = try await grpc.albumStub()
        var req = Barkcloud_Files_AddItemsToAlbumRequest()
        req.albumID = albumID
        req.fileIds = fileIDs
        _ = try await stub.addItemsToAlbum(req)
    }

    public func removeItems(albumID: String, fileIDs: [String]) async throws {
        let stub = try await grpc.albumStub()
        var req = Barkcloud_Files_RemoveItemsFromAlbumRequest()
        req.albumID = albumID
        req.fileIds = fileIDs
        _ = try await stub.removeItemsFromAlbum(req)
    }
}
