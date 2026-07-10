package com.barkfluff.BarkCloud.data.cloud

import barkcloud.files.FilesApiOuterClass.AlbumShareInfo
import barkcloud.files.FilesApiOuterClass.FolderShareInfo
import barkcloud.files.FilesApiOuterClass.OutgoingFolderShare
import barkcloud.files.FilesApiOuterClass.OutgoingShareFull
import barkcloud.files.FilesApiOuterClass.PublicDirEntry
import barkcloud.files.FilesApiOuterClass.PublicFileEntry
import barkcloud.files.FilesApiOuterClass.ShareInfo
import barkcloud.files.FilesApiOuterClass.SharedFolderEntry as ProtoSharedFolderEntry
import barkcloud.files.FilesApiOuterClass.SharedWithMeEntry as ProtoSharedWithMeEntry

// ============ Мои публичные (ShareInfo/FolderShareInfo/AlbumShareInfo) ============

enum class PublicShareKind { FILE, FOLDER, ALBUM }

/** Единая карточка публичной ссылки на файл/папку/альбом (зеркалит iOS PublicShareItem). */
data class PublicShareItem(
    val kind: PublicShareKind,
    val recordId: String, // id ссылки (share_id/folder_share_id/album_share_id) — используется для revoke
    val name: String,
    val clickCount: Long,
    val createdAtMillis: Long,
    val previewUrl: String?,
    val isVideo: Boolean,
) {
    val id: String get() = when (kind) {
        PublicShareKind.FILE -> "f:$recordId"
        PublicShareKind.FOLDER -> "d:$recordId"
        PublicShareKind.ALBUM -> "a:$recordId"
    }

    companion object {
        fun from(s: ShareInfo): PublicShareItem = PublicShareItem(
            kind = PublicShareKind.FILE,
            recordId = s.id,
            name = s.name,
            clickCount = s.clickCount,
            createdAtMillis = if (s.hasCreatedAt()) s.createdAt.toEpochMillis() else 0L,
            previewUrl = s.previewUrl.ifEmpty { null },
            isVideo = CloudMediaKind.from(s.mediaKind).isVideo,
        )

        fun from(s: FolderShareInfo): PublicShareItem = PublicShareItem(
            kind = PublicShareKind.FOLDER,
            recordId = s.id,
            name = s.name,
            clickCount = s.clickCount,
            createdAtMillis = if (s.hasCreatedAt()) s.createdAt.toEpochMillis() else 0L,
            previewUrl = null,
            isVideo = false,
        )

        fun from(s: AlbumShareInfo): PublicShareItem = PublicShareItem(
            kind = PublicShareKind.ALBUM,
            recordId = s.id,
            name = s.name,
            clickCount = s.clickCount,
            createdAtMillis = if (s.hasCreatedAt()) s.createdAt.toEpochMillis() else 0L,
            previewUrl = s.coverPreviewUrl.ifEmpty { null },
            isVideo = false,
        )
    }
}

// ============ Я поделился (гранты конкретным пользователям) ============

data class OutgoingRecipient(val grantId: String, val recipientUserId: Long, val sharedAtMillis: Long)

/** Плоский исходящий грант на файл, как приходит из `ListMyOutgoingSharesAll`. */
data class OutgoingShareRaw(
    val grantId: String,
    val asset: MediaAsset,
    val recipientUserId: Long,
    val sharedAtMillis: Long,
) {
    companion object {
        fun from(o: OutgoingShareFull): OutgoingShareRaw = OutgoingShareRaw(
            grantId = o.grantId,
            asset = MediaAsset.from(o.file),
            recipientUserId = o.recipientUserId,
            sharedAtMillis = if (o.hasSharedAt()) o.sharedAt.toEpochMillis() else 0L,
        )
    }
}

/** Страница исходящих файловых грантов с курсором пагинации. */
data class OutgoingSharesAllPage(
    val items: List<OutgoingShareRaw>,
    val nextCursorSharedAtMillis: Long?,
    val nextCursorGrantId: String,
) {
    val hasMore: Boolean get() = nextCursorSharedAtMillis != null
}

/** Исходящие гранты на файл, сгруппированные по получателям (пересобирается из [OutgoingShareRaw] во ViewModel). */
data class OutgoingShareGroup(val file: MediaAsset, val recipients: List<OutgoingRecipient>) {
    val id: String get() = file.id
}

/** Плоский исходящий грант на папку, как приходит из `ListMyOutgoingFolderShares` (без пагинации). */
data class OutgoingFolderShareRaw(
    val grantId: String,
    val directoryId: String,
    val name: String,
    val recipientUserId: Long,
    val sharedAtMillis: Long,
) {
    companion object {
        fun from(o: OutgoingFolderShare): OutgoingFolderShareRaw = OutgoingFolderShareRaw(
            grantId = o.grantId,
            directoryId = o.directoryId,
            name = o.name,
            recipientUserId = o.recipientUserId,
            sharedAtMillis = if (o.hasSharedAt()) o.sharedAt.toEpochMillis() else 0L,
        )
    }
}

data class OutgoingFolderShareGroup(val directoryId: String, val name: String, val recipients: List<OutgoingRecipient>) {
    val id: String get() = directoryId
}

// ============ Мне доступны (входящие гранты) ============

data class SharedWithMeEntry(
    val grantId: String,
    val asset: MediaAsset,
    val ownerUserId: Long,
    val sharedAtMillis: Long,
) {
    companion object {
        fun from(e: ProtoSharedWithMeEntry): SharedWithMeEntry = SharedWithMeEntry(
            grantId = e.grantId,
            asset = MediaAsset.from(e.file),
            ownerUserId = e.ownerUserId,
            sharedAtMillis = if (e.hasSharedAt()) e.sharedAt.toEpochMillis() else 0L,
        )
    }
}

/** Страница входящих файловых грантов с курсором пагинации. */
data class SharedWithMePage(
    val items: List<SharedWithMeEntry>,
    val nextCursorSharedAtMillis: Long?,
    val nextCursorGrantId: String,
) {
    val hasMore: Boolean get() = nextCursorSharedAtMillis != null
}

/** Входящий грант на папку, как приходит из `ListSharedFoldersWithMe` (без пагинации). */
data class SharedFolderEntry(
    val grantId: String,
    val directoryId: String,
    val name: String,
    val ownerUserId: Long,
    val sharedAtMillis: Long,
) {
    companion object {
        fun from(e: ProtoSharedFolderEntry): SharedFolderEntry = SharedFolderEntry(
            grantId = e.grantId,
            directoryId = e.directoryId,
            name = e.name,
            ownerUserId = e.ownerUserId,
            sharedAtMillis = if (e.hasSharedAt()) e.sharedAt.toEpochMillis() else 0L,
        )
    }
}

// ============ Навигация по чужой папке (ListSharedDirectory) ============

/** Подпапка внутри доступного мне дерева (зеркалит `PublicDirEntry`) — без parentId, только для листинга. */
data class PublicDir(val id: String, val name: String) {
    companion object {
        fun from(d: PublicDirEntry): PublicDir = PublicDir(d.id, d.name)
    }
}

/** Файл внутри доступного мне дерева — read-only, без entry_id (нет rename/move/delete). */
data class PublicFile(
    val id: String,
    val name: String,
    val kind: CloudMediaKind,
    val downloadUrl: String,
    val previewUrl: String?,
    val fileSize: Long,
    val imageWidth: Int,
    val imageHeight: Int,
) {
    companion object {
        fun from(f: PublicFileEntry): PublicFile = PublicFile(
            id = f.fileId,
            name = f.name,
            kind = CloudMediaKind.from(f.mediaKind),
            downloadUrl = f.downloadUrl,
            previewUrl = f.previewUrl.ifEmpty { null },
            fileSize = f.fileSize,
            imageWidth = f.imageWidth,
            imageHeight = f.imageHeight,
        )
    }
}

data class SharedDirListing(
    val found: Boolean,
    val directoryId: String,
    val name: String,
    val subdirs: List<PublicDir>,
    val files: List<PublicFile>,
)
