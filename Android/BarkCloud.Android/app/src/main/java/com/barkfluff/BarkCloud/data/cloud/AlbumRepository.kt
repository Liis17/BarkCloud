package com.barkfluff.BarkCloud.data.cloud

import barkcloud.files.FilesApiOuterClass.AddItemsToAlbumRequest
import barkcloud.files.FilesApiOuterClass.CreateAlbumRequest
import barkcloud.files.FilesApiOuterClass.DeleteAlbumRequest
import barkcloud.files.FilesApiOuterClass.ListAlbumItemsRequest
import barkcloud.files.FilesApiOuterClass.ListAlbumsRequest
import barkcloud.files.FilesApiOuterClass.MediaKind
import barkcloud.files.FilesApiOuterClass.RemoveItemsFromAlbumRequest
import barkcloud.files.FilesApiOuterClass.UpdateAlbumRequest
import com.barkfluff.BarkCloud.grpc.GrpcManager

/** Страница содержимого альбома с курсором пагинации. */
data class AlbumItemsPage(
    val items: List<MediaAsset>,
    val nextCursorAddedAtMillis: Long?,
    val nextCursorFileId: String,
) {
    val hasMore: Boolean get() = nextCursorAddedAtMillis != null
}

/** Доступ к сервису альбомов (`AlbumApi`). */
class AlbumRepository(private val grpc: GrpcManager) {

    suspend fun listAlbums(
        limit: Int = 50,
        cursorUpdatedAtMillis: Long? = null,
        cursorAlbumId: String = "",
    ): AlbumPage {
        val req = ListAlbumsRequest.newBuilder().setLimit(limit).setCursorAlbumId(cursorAlbumId)
        if (cursorUpdatedAtMillis != null) req.setCursorUpdatedAt(cursorUpdatedAtMillis.toTimestamp())
        val resp = grpc.albumStub().listAlbums(req.build())
        return AlbumPage(
            albums = resp.albumsList.map { AlbumCard.from(it) },
            nextCursorUpdatedAtMillis = if (resp.hasNextCursorUpdatedAt()) resp.nextCursorUpdatedAt.toEpochMillis() else null,
            nextCursorAlbumId = resp.nextCursorAlbumId,
        )
    }

    suspend fun listItems(
        albumId: String,
        kindFilter: CloudMediaKind? = null,
        limit: Int = 50,
        cursorAddedAtMillis: Long? = null,
        cursorFileId: String = "",
    ): AlbumItemsPage {
        val req = ListAlbumItemsRequest.newBuilder()
            .setAlbumId(albumId)
            .setLimit(limit)
            .setCursorFileId(cursorFileId)
        if (cursorAddedAtMillis != null) req.setCursorAddedAt(cursorAddedAtMillis.toTimestamp())
        when (kindFilter) {
            CloudMediaKind.VIDEO -> req.setKindFilter(MediaKind.MEDIA_KIND_VIDEO)
            CloudMediaKind.PHOTO -> req.setKindFilter(MediaKind.MEDIA_KIND_PHOTO)
            else -> Unit
        }
        val resp = grpc.albumStub().listAlbumItems(req.build())
        return AlbumItemsPage(
            items = resp.itemsList.map { MediaAsset.from(it.file) },
            nextCursorAddedAtMillis = if (resp.hasNextCursorAddedAt()) resp.nextCursorAddedAt.toEpochMillis() else null,
            nextCursorFileId = resp.nextCursorFileId,
        )
    }

    suspend fun createAlbum(name: String, description: String = ""): AlbumCard {
        val resp = grpc.albumStub().createAlbum(
            CreateAlbumRequest.newBuilder().setName(name).setDescription(description).build()
        )
        return AlbumCard.from(resp)
    }

    suspend fun updateAlbum(
        albumId: String,
        name: String? = null,
        description: String? = null,
        coverFileId: String? = null,
    ): AlbumCard {
        val req = UpdateAlbumRequest.newBuilder().setAlbumId(albumId)
        if (name != null) req.setName(name)
        if (description != null) req.setDescription(description)
        if (coverFileId != null) req.setCoverFileId(coverFileId)
        return AlbumCard.from(grpc.albumStub().updateAlbum(req.build()))
    }

    suspend fun deleteAlbum(albumId: String) {
        grpc.albumStub().deleteAlbum(DeleteAlbumRequest.newBuilder().setAlbumId(albumId).build())
    }

    suspend fun addItems(albumId: String, fileIds: List<String>) {
        grpc.albumStub().addItemsToAlbum(
            AddItemsToAlbumRequest.newBuilder().setAlbumId(albumId).addAllFileIds(fileIds).build()
        )
    }

    suspend fun removeItems(albumId: String, fileIds: List<String>) {
        grpc.albumStub().removeItemsFromAlbum(
            RemoveItemsFromAlbumRequest.newBuilder().setAlbumId(albumId).addAllFileIds(fileIds).build()
        )
    }
}
