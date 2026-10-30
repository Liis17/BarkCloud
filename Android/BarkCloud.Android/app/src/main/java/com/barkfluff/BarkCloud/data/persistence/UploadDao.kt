package com.barkfluff.BarkCloud.data.persistence

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import androidx.room.Update
import com.barkfluff.BarkCloud.data.upload.UploadJob
import com.barkfluff.BarkCloud.data.upload.UploadPhase
import kotlinx.coroutines.flow.Flow

@Dao
interface UploadDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun insert(job: UploadJob)

    @Update
    suspend fun update(job: UploadJob)

    @Query("SELECT * FROM upload_jobs WHERE phase NOT IN ('COMPLETED', 'CANCELLED') ORDER BY createdAtMillis")
    fun observeVisible(): Flow<List<UploadJob>>

    @Query("SELECT * FROM upload_jobs WHERE phase != 'CANCELLED' ORDER BY createdAtMillis")
    fun observeRecent(): Flow<List<UploadJob>>

    @Query("SELECT * FROM upload_jobs WHERE phase IN ('QUEUED', 'HASHING', 'CREATING_SESSION', 'UPLOADING', 'COMPLETING', 'PROCESSING', 'ATTACHING') AND (:afterId IS NULL OR id > :afterId) ORDER BY id LIMIT :limit")
    suspend fun nextActivePage(afterId: String?, limit: Int): List<UploadJob>

    @Query("SELECT COUNT(*) FROM upload_jobs WHERE phase IN ('QUEUED', 'HASHING', 'CREATING_SESSION', 'UPLOADING', 'COMPLETING', 'PROCESSING', 'ATTACHING')")
    suspend fun activeCount(): Int

    @Query("SELECT COUNT(*) FROM upload_jobs WHERE source = 'BACKUP' AND stagedFilePath IS NOT NULL AND phase NOT IN ('COMPLETED', 'CANCELLED')")
    suspend fun stagedBackupCount(): Int

    @Query("SELECT * FROM upload_jobs WHERE source = 'BACKUP' AND stagedFilePath IS NOT NULL AND phase IN ('FAILED', 'PAUSED', 'ATTACHING', 'UPLOADED_NOT_ATTACHED') ORDER BY createdAtMillis, id LIMIT :limit")
    suspend fun inactiveStagedBackupPage(limit: Int): List<UploadJob>

    @Query("SELECT * FROM upload_jobs WHERE source = 'BACKUP' AND stagedFilePath IS NOT NULL AND phase IN ('QUEUED', 'FAILED', 'PAUSED', 'UPLOADED_NOT_ATTACHED') ORDER BY createdAtMillis DESC, id DESC LIMIT :limit")
    suspend fun releasableBackupStagePage(limit: Int): List<UploadJob>

    @Query("UPDATE upload_jobs SET stagedFilePath = NULL WHERE id = :id AND source = 'BACKUP' AND stagedFilePath = :path AND phase = :expectedPhase AND phase IN ('QUEUED', 'FAILED', 'PAUSED', 'ATTACHING', 'UPLOADED_NOT_ATTACHED')")
    suspend fun releaseBackupStage(id: String, path: String, expectedPhase: UploadPhase): Int

    @Query("SELECT * FROM upload_jobs WHERE phase = 'CANCELLED' AND sessionId IS NOT NULL")
    suspend fun cancelledWithSession(): List<UploadJob>

    /** Активные задачи старого (V1) формата, включая устаревшие фазы UPLOADED/PAUSED. */
    @Query("SELECT * FROM upload_jobs WHERE phase IN ('QUEUED', 'UPLOADING', 'UPLOADED', 'ATTACHING', 'PAUSED')")
    suspend fun v1ActiveJobs(): List<UploadJob>

    @Query("UPDATE upload_jobs SET phase = 'CANCELLED', errorMessage = :message WHERE phase IN ('QUEUED', 'UPLOADING', 'UPLOADED', 'ATTACHING', 'PAUSED')")
    suspend fun cancelV1Jobs(message: String)

    @Query("SELECT * FROM upload_jobs WHERE id = :id LIMIT 1")
    suspend fun byId(id: String): UploadJob?

    @Query("SELECT * FROM upload_jobs WHERE source = 'BACKUP' AND mediaKey = :mediaKey ORDER BY createdAtMillis DESC LIMIT 1")
    suspend fun backupByMediaKey(mediaKey: String): UploadJob?

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertBackupIfAbsent(job: UploadJob): Long

    @Query("UPDATE upload_jobs SET phase = :phase, errorMessage = :errorMessage WHERE id = :id AND phase NOT IN ('CANCELLED', 'COMPLETED') AND (phase != 'PAUSED' OR :phase = 'PAUSED')")
    suspend fun setPhase(id: String, phase: UploadPhase, errorMessage: String? = null)

    @Query("UPDATE upload_jobs SET phase = :phase, errorMessage = NULL WHERE id = :id AND phase = :expectedPhase AND phase IN ('FAILED', 'UPLOADED_NOT_ATTACHED', 'PAUSED')")
    suspend fun retry(id: String, expectedPhase: UploadPhase, phase: UploadPhase): Int

    @Query("UPDATE upload_jobs SET bytesSent = :bytesSent, bytesTotal = :bytesTotal, phase = :phase WHERE id = :id AND phase NOT IN ('CANCELLED', 'COMPLETED', 'PAUSED')")
    suspend fun setProgress(id: String, bytesSent: Long, bytesTotal: Long, phase: UploadPhase)

    @Query("UPDATE upload_jobs SET mediaHash = :mediaHash, bytesTotal = :bytesTotal WHERE id = :id AND phase NOT IN ('CANCELLED', 'COMPLETED', 'PAUSED')")
    suspend fun setHash(id: String, mediaHash: String, bytesTotal: Long)

    @Query("UPDATE upload_jobs SET phase = 'CREATING_SESSION', idempotencyKey = :idempotencyKey, sessionId = CASE WHEN :replaceExisting THEN NULL ELSE sessionId END, partSize = CASE WHEN :replaceExisting THEN 0 ELSE partSize END, preparedFileId = CASE WHEN :replaceExisting THEN NULL ELSE preparedFileId END, bytesSent = 0 WHERE id = :id AND phase NOT IN ('CANCELLED', 'COMPLETED', 'PAUSED')")
    suspend fun prepareUploadSession(id: String, idempotencyKey: String, replaceExisting: Boolean): Int

    @Query("UPDATE upload_jobs SET sessionId = :sessionId, idempotencyKey = :idempotencyKey, preparedFileId = :fileId, partSize = :partSize WHERE id = :id AND phase = 'CREATING_SESSION'")
    suspend fun bindSession(id: String, sessionId: String, idempotencyKey: String, fileId: String, partSize: Long): Int

    @Query("UPDATE upload_jobs SET phase = 'FAILED', errorMessage = :errorMessage, idempotencyKey = CASE WHEN sessionId IS NULL THEN NULL ELSE idempotencyKey END WHERE id = :id AND phase NOT IN ('CANCELLED', 'COMPLETED', 'PAUSED')")
    suspend fun failAfterQuota(id: String, errorMessage: String?): Int

    @Query("UPDATE upload_jobs SET sessionId = NULL WHERE id = :id")
    suspend fun clearSession(id: String)

    @Query("UPDATE upload_jobs SET phase = 'PAUSED' WHERE source = 'BACKUP' AND phase IN ('QUEUED', 'HASHING', 'CREATING_SESSION', 'UPLOADING', 'COMPLETING', 'PROCESSING', 'ATTACHING')")
    suspend fun pauseBackup()

    @Query("UPDATE upload_jobs SET phase = CASE WHEN sessionId IS NOT NULL THEN 'UPLOADING' ELSE 'QUEUED' END, errorMessage = NULL WHERE source = 'BACKUP' AND phase = 'PAUSED'")
    suspend fun resumeBackup()

    @Query("UPDATE upload_jobs SET phase = 'PAUSED' WHERE id = :id AND source = 'BACKUP' AND phase IN ('QUEUED', 'HASHING', 'CREATING_SESSION', 'UPLOADING', 'COMPLETING', 'PROCESSING', 'ATTACHING')")
    suspend fun pauseBackupJob(id: String): Int

    @Query("UPDATE upload_jobs SET phase = 'QUEUED', stagedFilePath = :path, mediaHash = :hash, bytesTotal = :bytesTotal, mimeType = :mimeType, idempotencyKey = CASE WHEN :resetSession THEN NULL ELSE idempotencyKey END, sessionId = CASE WHEN :resetSession THEN NULL ELSE sessionId END, partSize = CASE WHEN :resetSession THEN 0 ELSE partSize END, preparedFileId = CASE WHEN :resetSession THEN NULL ELSE preparedFileId END, bytesSent = 0, errorMessage = NULL WHERE id = :id AND source = 'BACKUP' AND phase = 'HASHING'")
    suspend fun setBackupPrepared(id: String, path: String, hash: String, bytesTotal: Long, mimeType: String?, resetSession: Boolean): Int

    @Query("UPDATE upload_jobs SET fileName = :fileName, mimeType = :mimeType, bytesTotal = :bytesTotal, bytesSent = 0, stagedFilePath = NULL, mediaHash = NULL, idempotencyKey = NULL, sessionId = NULL, partSize = 0, preparedFileId = NULL, phase = 'QUEUED', errorMessage = NULL WHERE id = :id AND source = 'BACKUP' AND phase NOT IN ('CANCELLED', 'COMPLETED', 'PAUSED')")
    suspend fun resetBackupForChangedMedia(id: String, fileName: String, mimeType: String?, bytesTotal: Long): Int

    @Query("UPDATE upload_jobs SET stagedFilePath = NULL WHERE id = :id AND phase NOT IN ('CANCELLED', 'COMPLETED')")
    suspend fun clearStagedPath(id: String): Int

    @Query("UPDATE upload_jobs SET phase = 'CANCELLED', stagedFilePath = NULL WHERE id = :id AND phase NOT IN ('COMPLETED', 'CANCELLED')")
    suspend fun cancel(id: String): Int

    @Query("UPDATE upload_jobs SET phase = 'COMPLETED', completedAtMillis = :completedAtMillis, errorMessage = NULL, stagedFilePath = NULL WHERE id = :id AND phase NOT IN ('CANCELLED', 'COMPLETED', 'PAUSED')")
    suspend fun markCompleted(id: String, completedAtMillis: Long): Int

    @Query("UPDATE upload_jobs SET phase = 'FAILED', stagedFilePath = NULL, sessionId = NULL, idempotencyKey = NULL, preparedFileId = NULL, errorMessage = :errorMessage WHERE id = :id AND source = 'BACKUP' AND phase NOT IN ('CANCELLED', 'COMPLETED', 'PAUSED')")
    suspend fun failBackupAndDropStage(id: String, errorMessage: String?): Int

    @Query("DELETE FROM upload_jobs WHERE phase = 'COMPLETED' AND completedAtMillis < :beforeMillis")
    suspend fun deleteCompletedBefore(beforeMillis: Long)

    @Query("DELETE FROM upload_jobs")
    suspend fun deleteAll()
}
