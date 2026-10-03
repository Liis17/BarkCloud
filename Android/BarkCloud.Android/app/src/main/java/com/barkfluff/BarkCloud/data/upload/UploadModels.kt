package com.barkfluff.BarkCloud.data.upload

import androidx.room.Entity
import androidx.room.PrimaryKey

enum class UploadSource { MANUAL, GALLERY, ALBUM, SHARE, BACKUP }
enum class UploadDestination { SYSTEM_BY_MEDIA_KIND, DIRECTORY }

/**
 * Фазы Upload 2.0: `QUEUED → HASHING → CREATING_SESSION → UPLOADING → COMPLETING →
 * PROCESSING → ATTACHING → COMPLETED`. `UPLOADED_NOT_ATTACHED` — байты уже на сервере
 * (`ready`), упало post-ready действие; retry повторяет только привязку.
 * `UPLOADED` — устаревшая V1-фаза, оставлена только для чтения старых строк БД
 * (одноразовая миграция V1→V2 отменяет такие задачи).
 */
enum class UploadPhase {
    QUEUED, HASHING, CREATING_SESSION, UPLOADING, COMPLETING, PROCESSING, ATTACHING,
    UPLOADED, UPLOADED_NOT_ATTACHED, PAUSED, FAILED, COMPLETED, CANCELLED,
}

@Entity(tableName = "upload_jobs")
data class UploadJob(
    @PrimaryKey val id: String,
    val source: UploadSource,
    val stagedFilePath: String?,
    val mediaKey: String?,
    val mediaHash: String?,
    val fileName: String,
    val mimeType: String?,
    val destination: UploadDestination,
    val directoryId: String?,
    val albumId: String?,
    val phase: UploadPhase,
    val idempotencyKey: String?,
    val sessionId: String?,
    val partSize: Long,
    val preparedFileId: String?,
    val bytesTotal: Long,
    val bytesSent: Long,
    val errorMessage: String?,
    val createdAtMillis: Long,
    val completedAtMillis: Long?,
)
