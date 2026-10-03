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

    @Query("SELECT * FROM upload_jobs WHERE phase IN ('QUEUED', 'HASHING', 'CREATING_SESSION', 'UPLOADING', 'COMPLETING', 'PROCESSING', 'ATTACHING') ORDER BY createdAtMillis")
    suspend fun activeJobs(): List<UploadJob>

    @Query("SELECT * FROM upload_jobs WHERE phase IN ('QUEUED', 'HASHING', 'CREATING_SESSION', 'UPLOADING', 'COMPLETING', 'PROCESSING', 'ATTACHING') AND source = 'BACKUP'")
    suspend fun activeBackup(): List<UploadJob>

    @Query("SELECT * FROM upload_jobs WHERE phase = 'CANCELLED' AND sessionId IS NOT NULL")
    suspend fun cancelledWithSession(): List<UploadJob>

    /** Активные задачи старого (V1) формата, включая устаревшие фазы UPLOADED/PAUSED. */
    @Query("SELECT * FROM upload_jobs WHERE phase IN ('QUEUED', 'UPLOADING', 'UPLOADED', 'ATTACHING', 'PAUSED')")
    suspend fun v1ActiveJobs(): List<UploadJob>

    @Query("UPDATE upload_jobs SET phase = 'CANCELLED', errorMessage = :message WHERE phase IN ('QUEUED', 'UPLOADING', 'UPLOADED', 'ATTACHING', 'PAUSED')")
    suspend fun cancelV1Jobs(message: String)

    @Query("SELECT * FROM upload_jobs WHERE id = :id LIMIT 1")
    suspend fun byId(id: String): UploadJob?

    @Query("UPDATE upload_jobs SET phase = :phase, errorMessage = :errorMessage WHERE id = :id")
    suspend fun setPhase(id: String, phase: UploadPhase, errorMessage: String? = null)

    @Query("UPDATE upload_jobs SET bytesSent = :bytesSent, bytesTotal = :bytesTotal, phase = :phase WHERE id = :id")
    suspend fun setProgress(id: String, bytesSent: Long, bytesTotal: Long, phase: UploadPhase)

    @Query("UPDATE upload_jobs SET mediaHash = :mediaHash, bytesTotal = :bytesTotal WHERE id = :id")
    suspend fun setHash(id: String, mediaHash: String, bytesTotal: Long)

    @Query("UPDATE upload_jobs SET sessionId = :sessionId, idempotencyKey = :idempotencyKey, preparedFileId = :fileId, partSize = :partSize WHERE id = :id")
    suspend fun bindSession(id: String, sessionId: String, idempotencyKey: String, fileId: String, partSize: Long)

    @Query("UPDATE upload_jobs SET sessionId = NULL WHERE id = :id")
    suspend fun clearSession(id: String)

    @Query("UPDATE upload_jobs SET phase = 'PAUSED' WHERE source = 'BACKUP' AND phase IN ('QUEUED', 'HASHING', 'CREATING_SESSION', 'UPLOADING', 'COMPLETING', 'PROCESSING', 'ATTACHING')")
    suspend fun pauseBackup()

    @Query("UPDATE upload_jobs SET phase = 'QUEUED', errorMessage = NULL WHERE source = 'BACKUP' AND phase = 'PAUSED'")
    suspend fun resumeBackup()

    @Query("UPDATE upload_jobs SET phase = 'COMPLETED', completedAtMillis = :completedAtMillis, errorMessage = NULL WHERE id = :id")
    suspend fun markCompleted(id: String, completedAtMillis: Long)

    @Query("DELETE FROM upload_jobs WHERE phase = 'COMPLETED' AND completedAtMillis < :beforeMillis")
    suspend fun deleteCompletedBefore(beforeMillis: Long)

    @Query("DELETE FROM upload_jobs")
    suspend fun deleteAll()
}
