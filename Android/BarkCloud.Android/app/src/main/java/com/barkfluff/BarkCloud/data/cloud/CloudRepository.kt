package com.barkfluff.BarkCloud.data.cloud

import android.net.Uri
import barkcloud.files.FilesApiOuterClass.AddFavoriteRequest
import barkcloud.files.FilesApiOuterClass.AttachFileRequest
import barkcloud.files.FilesApiOuterClass.CheckFileHashesRequest
import barkcloud.files.FilesApiOuterClass.CreateDirectoryRequest
import barkcloud.files.FilesApiOuterClass.DeleteDirectoryRequest
import barkcloud.files.FilesApiOuterClass.DeleteFileEntryRequest
import barkcloud.files.FilesApiOuterClass.DeleteFromTrashRequest
import barkcloud.files.FilesApiOuterClass.EmptyTrashRequest
import barkcloud.files.FilesApiOuterClass.GetPathRequest
import barkcloud.files.FilesApiOuterClass.ListDirectoryRequest
import barkcloud.files.FilesApiOuterClass.ListFavoritesRequest
import barkcloud.files.FilesApiOuterClass.ListTrashRequest
import barkcloud.files.FilesApiOuterClass.ListUserMediaRequest
import barkcloud.files.FilesApiOuterClass.MediaKind
import barkcloud.files.FilesApiOuterClass.MoveDirectoryRequest
import barkcloud.files.FilesApiOuterClass.MoveFileEntryRequest
import barkcloud.files.FilesApiOuterClass.RemoveFavoriteRequest
import barkcloud.files.FilesApiOuterClass.RenameDirectoryRequest
import barkcloud.files.FilesApiOuterClass.RenameFileEntryRequest
import barkcloud.files.FilesApiOuterClass.RestoreFromTrashRequest
import barkcloud.files.FilesApiOuterClass.UploadFileType
import com.barkfluff.BarkCloud.grpc.GrpcManager
import com.barkfluff.BarkCloud.net.FileTransferService

/**
 * Доступ к сервису Files: галерея (`ListUserMedia`), каталоги (`CloudApi`), корзина,
 * избранное и загрузка файлов (через [FileTransferService]).
 */
class CloudRepository(
    private val grpc: GrpcManager,
    val transfer: FileTransferService,
) {

    // MARK: Галерея

    suspend fun listUserMedia(
        kind: CloudMediaKind,
        limit: Int = 50,
        cursorCreatedAtMillis: Long? = null,
        cursorFileId: String = "",
    ): MediaPage {
        val req = ListUserMediaRequest.newBuilder()
            .setKind(if (kind == CloudMediaKind.VIDEO) MediaKind.MEDIA_KIND_VIDEO else MediaKind.MEDIA_KIND_PHOTO)
            .setLimit(limit)
            .setCursorFileId(cursorFileId)
        if (cursorCreatedAtMillis != null) req.setCursorCreatedAt(cursorCreatedAtMillis.toTimestamp())
        val resp = grpc.cloudStub().listUserMedia(req.build())
        return MediaPage(
            items = resp.itemsList.map { MediaAsset.from(it.file) },
            nextCursorCreatedAtMillis = if (resp.hasNextCursorCreatedAt()) resp.nextCursorCreatedAt.toEpochMillis() else null,
            nextCursorFileId = resp.nextCursorFileId,
        )
    }

    /** Пакетная проверка наличия файлов в облаке по SHA256-хешам. хеш → есть ли в облаке. */
    suspend fun checkFileHashes(hashes: List<String>): Map<String, Boolean> {
        if (hashes.isEmpty()) return emptyMap()
        val resp = grpc.filesStub().checkFileHashes(
            CheckFileHashesRequest.newBuilder().addAllFileHashes(hashes).build()
        )
        return resp.resultsList.associate { it.fileHash to it.exists }
    }

    // MARK: Каталоги

    /** Содержимое папки с полной информацией о файлах. `""` = корень. */
    suspend fun listDirectory(directoryId: String): CloudListing {
        val resp = grpc.cloudStub().listDirectoryDetailed(
            ListDirectoryRequest.newBuilder().setDirectoryId(directoryId).build()
        )
        return CloudListing(
            subdirs = resp.subdirsList.map { CloudDirectory.from(it) },
            files = resp.filesList.map { CloudFileEntry.from(it) },
        )
    }

    /** Хлебные крошки до папки (от корня, не включая саму папку). */
    suspend fun path(directoryId: String): List<PathCrumb> {
        val resp = grpc.cloudStub().getPath(
            GetPathRequest.newBuilder().setDirectoryId(directoryId).build()
        )
        return resp.segmentsList.map { PathCrumb(it.id, it.name) }
    }

    suspend fun createDirectory(parentId: String, name: String): CloudDirectory {
        val resp = grpc.cloudStub().createDirectory(
            CreateDirectoryRequest.newBuilder().setParentId(parentId).setName(name).build()
        )
        return CloudDirectory.from(resp)
    }

    suspend fun renameDirectory(directoryId: String, newName: String) {
        grpc.cloudStub().renameDirectory(
            RenameDirectoryRequest.newBuilder().setDirectoryId(directoryId).setNewName(newName).build()
        )
    }

    suspend fun moveDirectory(directoryId: String, newParentId: String) {
        grpc.cloudStub().moveDirectory(
            MoveDirectoryRequest.newBuilder().setDirectoryId(directoryId).setNewParentId(newParentId).build()
        )
    }

    suspend fun deleteDirectory(directoryId: String) {
        grpc.cloudStub().deleteDirectory(
            DeleteDirectoryRequest.newBuilder().setDirectoryId(directoryId).build()
        )
    }

    // MARK: Записи о файлах

    suspend fun attachFile(fileId: String, directoryId: String, name: String) {
        grpc.cloudStub().attachFile(
            AttachFileRequest.newBuilder().setFileId(fileId).setDirectoryId(directoryId).setName(name).build()
        )
    }

    suspend fun renameFileEntry(entryId: String, newName: String) {
        grpc.cloudStub().renameFileEntry(
            RenameFileEntryRequest.newBuilder().setEntryId(entryId).setNewName(newName).build()
        )
    }

    suspend fun moveFileEntry(entryId: String, newDirectoryId: String) {
        grpc.cloudStub().moveFileEntry(
            MoveFileEntryRequest.newBuilder().setEntryId(entryId).setNewDirectoryId(newDirectoryId).build()
        )
    }

    suspend fun deleteFileEntry(entryId: String) {
        grpc.cloudStub().deleteFileEntry(
            DeleteFileEntryRequest.newBuilder().setEntryId(entryId).build()
        )
    }

    // MARK: Корзина

    suspend fun listTrash(
        limit: Int = 50,
        cursorDeletedAtMillis: Long? = null,
        cursorEntryId: String = "",
    ): TrashPage {
        val req = ListTrashRequest.newBuilder().setLimit(limit).setCursorEntryId(cursorEntryId)
        if (cursorDeletedAtMillis != null) req.setCursorDeletedAt(cursorDeletedAtMillis.toTimestamp())
        val resp = grpc.cloudStub().listTrash(req.build())
        return TrashPage(
            items = resp.itemsList.map { TrashItem.from(it) },
            nextCursorDeletedAtMillis = if (resp.hasNextCursorDeletedAt()) resp.nextCursorDeletedAt.toEpochMillis() else null,
            nextCursorEntryId = resp.nextCursorEntryId,
        )
    }

    suspend fun restoreFromTrash(entryId: String) {
        grpc.cloudStub().restoreFromTrash(
            RestoreFromTrashRequest.newBuilder().setEntryId(entryId).build()
        )
    }

    suspend fun deleteFromTrash(entryId: String) {
        grpc.cloudStub().deleteFromTrash(
            DeleteFromTrashRequest.newBuilder().setEntryId(entryId).build()
        )
    }

    suspend fun emptyTrash() {
        grpc.cloudStub().emptyTrash(EmptyTrashRequest.getDefaultInstance())
    }

    // MARK: Избранное

    suspend fun addFavorite(fileId: String) {
        grpc.cloudStub().addFavorite(AddFavoriteRequest.newBuilder().setFileId(fileId).build())
    }

    suspend fun removeFavorite(fileId: String) {
        grpc.cloudStub().removeFavorite(RemoveFavoriteRequest.newBuilder().setFileId(fileId).build())
    }

    suspend fun listFavorites(
        limit: Int = 50,
        cursorFavoritedAtMillis: Long? = null,
        cursorFileId: String = "",
    ): FavoritesPage {
        val req = ListFavoritesRequest.newBuilder().setLimit(limit).setCursorFileId(cursorFileId)
        if (cursorFavoritedAtMillis != null) req.setCursorFavoritedAt(cursorFavoritedAtMillis.toTimestamp())
        val resp = grpc.cloudStub().listFavorites(req.build())
        return FavoritesPage(
            items = resp.itemsList.map { FavoriteItem.from(it) },
            nextCursorFavoritedAtMillis = if (resp.hasNextCursorFavoritedAt()) resp.nextCursorFavoritedAt.toEpochMillis() else null,
            nextCursorFileId = resp.nextCursorFileId,
        )
    }

    // MARK: Загрузка

    /**
     * Загрузить файл в облако. Если задан [directoryId] — привязать к папке.
     * Возвращает `file_id` блоба (из ответа сервера; учитывает дедупликацию).
     */
    suspend fun uploadFile(uri: Uri, fileName: String, directoryId: String? = null): String {
        val target = transfer.getUploadUrl(UploadFileType.CLOUD_FILE)
        val fileId = transfer.upload(uri, fileName, target.url)
        if (directoryId != null) attachFile(fileId, directoryId, fileName)
        return fileId
    }
}
