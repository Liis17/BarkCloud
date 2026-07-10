package com.barkfluff.BarkCloud.data.upload

import androidx.room.Entity
import androidx.room.PrimaryKey

enum class UploadSource { MANUAL, GALLERY, ALBUM, SHARE, BACKUP }
enum class UploadDestination { SYSTEM_BY_MEDIA_KIND, DIRECTORY }
enum class UploadPhase { QUEUED, UPLOADING, UPLOADED, ATTACHING, PAUSED, FAILED, COMPLETED, CANCELLED }

@Entity(tableName = "upload_jobs")
data class UploadJob(
    @PrimaryKey val id: String,
    val source: UploadSource,
    val sourceUri: String?,
    val stagedFilePath: String?,
    val mediaKey: String?,
    val mediaHash: String?,
    val fileName: String,
    val mimeType: String?,
    val destination: UploadDestination,
    val directoryId: String?,
    val albumId: String?,
    val phase: UploadPhase,
    val preparedFileId: String?,
    val uploadUrl: String?,
    val bytesTotal: Long,
    val bytesSent: Long,
    val errorMessage: String?,
    val createdAtMillis: Long,
    val completedAtMillis: Long?,
)
