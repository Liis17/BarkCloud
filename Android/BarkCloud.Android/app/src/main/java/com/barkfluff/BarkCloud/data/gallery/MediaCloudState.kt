package com.barkfluff.BarkCloud.data.gallery

import androidx.room.Entity
import androidx.room.PrimaryKey

enum class MediaCloudStatus { CHECKING, NOT_IN_CLOUD, QUEUED, UPLOADING, IN_CLOUD, ERROR }

@Entity(tableName = "media_cloud_states")
data class MediaCloudState(
    @PrimaryKey val mediaKey: String,
    val mediaId: Long,
    val isVideo: Boolean,
    val dateModifiedSeconds: Long,
    val sizeBytes: Long,
    val hash: String?,
    val status: MediaCloudStatus,
    val cloudFileId: String?,
)

internal fun cloudStateOf(
    media: DeviceMedia,
    hash: String?,
    status: MediaCloudStatus,
    cloudFileId: String? = null,
): MediaCloudState = MediaCloudState(
    mediaKey = media.mediaKey,
    mediaId = media.id,
    isVideo = media.isVideo,
    dateModifiedSeconds = media.dateModifiedSeconds,
    sizeBytes = media.sizeBytes,
    hash = hash,
    status = status,
    cloudFileId = cloudFileId,
)
