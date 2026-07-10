package com.barkfluff.BarkCloud.data.cloud

import barkcloud.files.FilesApiOuterClass.GetSharedFileDownloadUrlRequest
import barkcloud.files.FilesApiOuterClass.ListMyAlbumSharesRequest
import barkcloud.files.FilesApiOuterClass.ListMyFolderSharesRequest
import barkcloud.files.FilesApiOuterClass.ListMyOutgoingFolderSharesRequest
import barkcloud.files.FilesApiOuterClass.ListMyOutgoingSharesAllRequest
import barkcloud.files.FilesApiOuterClass.ListMySharesRequest
import barkcloud.files.FilesApiOuterClass.ListSharedDirectoryRequest
import barkcloud.files.FilesApiOuterClass.ListSharedFoldersWithMeRequest
import barkcloud.files.FilesApiOuterClass.ListSharedWithMeRequest
import barkcloud.files.FilesApiOuterClass.RevokeAlbumShareRequest
import barkcloud.files.FilesApiOuterClass.RevokeFolderShareRequest
import barkcloud.files.FilesApiOuterClass.RevokeFolderUserShareRequest
import barkcloud.files.FilesApiOuterClass.RevokeShareRequest
import barkcloud.files.FilesApiOuterClass.RevokeUserShareRequest
import barkcloud.files.FilesApiOuterClass.ShareFileWithUserRequest
import barkcloud.files.FilesApiOuterClass.ShareFolderWithUserRequest
import com.barkfluff.BarkCloud.grpc.GrpcManager

/**
 * Доступ к шаринг-эндпоинтам `CloudApi`: публичные ссылки, гранты конкретным
 * пользователям, навигация по доступной мне чужой папке. Отдельно от
 * [CloudRepository] (тот посвящён «своим» файлам/папкам/корзине/избранному).
 */
class SharedRepository(private val grpc: GrpcManager) {

    // MARK: Мои публичные

    suspend fun listMyShares(limit: Int = 200): List<PublicShareItem> =
        grpc.cloudStub().listMyShares(
            ListMySharesRequest.newBuilder().setLimit(limit).build()
        ).sharesList.map { PublicShareItem.from(it) }

    suspend fun listMyFolderShares(limit: Int = 200): List<PublicShareItem> =
        grpc.cloudStub().listMyFolderShares(
            ListMyFolderSharesRequest.newBuilder().setLimit(limit).build()
        ).sharesList.map { PublicShareItem.from(it) }

    suspend fun listMyAlbumShares(limit: Int = 200): List<PublicShareItem> =
        grpc.cloudStub().listMyAlbumShares(
            ListMyAlbumSharesRequest.newBuilder().setLimit(limit).build()
        ).sharesList.map { PublicShareItem.from(it) }

    suspend fun revokeShare(shareId: String) {
        grpc.cloudStub().revokeShare(RevokeShareRequest.newBuilder().setShareId(shareId).build())
    }

    suspend fun revokeFolderShare(folderShareId: String) {
        grpc.cloudStub().revokeFolderShare(
            RevokeFolderShareRequest.newBuilder().setFolderShareId(folderShareId).build()
        )
    }

    suspend fun revokeAlbumShare(albumShareId: String) {
        grpc.cloudStub().revokeAlbumShare(
            RevokeAlbumShareRequest.newBuilder().setAlbumShareId(albumShareId).build()
        )
    }

    // MARK: Я поделился

    suspend fun listMyOutgoingSharesAll(
        limit: Int = 60,
        cursorSharedAtMillis: Long? = null,
        cursorGrantId: String = "",
    ): OutgoingSharesAllPage {
        val req = ListMyOutgoingSharesAllRequest.newBuilder().setLimit(limit).setCursorGrantId(cursorGrantId)
        if (cursorSharedAtMillis != null) req.setCursorSharedAt(cursorSharedAtMillis.toTimestamp())
        val resp = grpc.cloudStub().listMyOutgoingSharesAll(req.build())
        return OutgoingSharesAllPage(
            items = resp.itemsList.map { OutgoingShareRaw.from(it) },
            nextCursorSharedAtMillis = if (resp.hasNextCursorSharedAt()) resp.nextCursorSharedAt.toEpochMillis() else null,
            nextCursorGrantId = resp.nextCursorGrantId,
        )
    }

    suspend fun listMyOutgoingFolderShares(): List<OutgoingFolderShareRaw> =
        grpc.cloudStub().listMyOutgoingFolderShares(
            ListMyOutgoingFolderSharesRequest.getDefaultInstance()
        ).itemsList.map { OutgoingFolderShareRaw.from(it) }

    suspend fun shareFileWithUser(fileId: String, recipientUserId: Long) {
        grpc.cloudStub().shareFileWithUser(
            ShareFileWithUserRequest.newBuilder().setFileId(fileId).setRecipientUserId(recipientUserId).build()
        )
    }

    suspend fun shareFolderWithUser(directoryId: String, recipientUserId: Long) {
        grpc.cloudStub().shareFolderWithUser(
            ShareFolderWithUserRequest.newBuilder().setDirectoryId(directoryId).setRecipientUserId(recipientUserId).build()
        )
    }

    suspend fun revokeUserShare(grantId: String) {
        grpc.cloudStub().revokeUserShare(RevokeUserShareRequest.newBuilder().setGrantId(grantId).build())
    }

    suspend fun revokeFolderUserShare(grantId: String) {
        grpc.cloudStub().revokeFolderUserShare(RevokeFolderUserShareRequest.newBuilder().setGrantId(grantId).build())
    }

    // MARK: Мне доступны

    suspend fun listSharedWithMe(
        limit: Int = 60,
        cursorSharedAtMillis: Long? = null,
        cursorGrantId: String = "",
    ): SharedWithMePage {
        val req = ListSharedWithMeRequest.newBuilder().setLimit(limit).setCursorGrantId(cursorGrantId)
        if (cursorSharedAtMillis != null) req.setCursorSharedAt(cursorSharedAtMillis.toTimestamp())
        val resp = grpc.cloudStub().listSharedWithMe(req.build())
        return SharedWithMePage(
            items = resp.itemsList.map { SharedWithMeEntry.from(it) },
            nextCursorSharedAtMillis = if (resp.hasNextCursorSharedAt()) resp.nextCursorSharedAt.toEpochMillis() else null,
            nextCursorGrantId = resp.nextCursorGrantId,
        )
    }

    suspend fun listSharedFoldersWithMe(): List<SharedFolderEntry> =
        grpc.cloudStub().listSharedFoldersWithMe(
            ListSharedFoldersWithMeRequest.getDefaultInstance()
        ).itemsList.map { SharedFolderEntry.from(it) }

    suspend fun getSharedFileDownloadUrl(fileId: String): String =
        grpc.cloudStub().getSharedFileDownloadUrl(
            GetSharedFileDownloadUrlRequest.newBuilder().setFileId(fileId).build()
        ).downloadUrl

    suspend fun listSharedDirectory(directoryId: String): SharedDirListing {
        val resp = grpc.cloudStub().listSharedDirectory(
            ListSharedDirectoryRequest.newBuilder().setDirectoryId(directoryId).build()
        )
        return SharedDirListing(
            found = resp.found,
            directoryId = resp.directoryId,
            name = resp.name,
            subdirs = resp.subdirsList.map { PublicDir.from(it) },
            files = resp.filesList.map { PublicFile.from(it) },
        )
    }
}
