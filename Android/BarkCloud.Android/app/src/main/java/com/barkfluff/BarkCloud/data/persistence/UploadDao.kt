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

    @Query("SELECT * FROM upload_jobs WHERE phase IN ('QUEUED', 'UPLOADING', 'UPLOADED', 'ATTACHING') ORDER BY createdAtMillis LIMIT 1")
    suspend fun nextActive(): UploadJob?

    @Query("SELECT * FROM upload_jobs WHERE phase IN ('QUEUED', 'UPLOADING', 'UPLOADED', 'ATTACHING') ORDER BY createdAtMillis")
    suspend fun activeJobs(): List<UploadJob>

    @Query("SELECT * FROM upload_jobs WHERE phase = 'PAUSED' AND source = 'BACKUP' ORDER BY createdAtMillis")
    suspend fun pausedBackup(): List<UploadJob>

    @Query("SELECT * FROM upload_jobs WHERE phase IN ('QUEUED', 'UPLOADING', 'UPLOADED', 'ATTACHING') AND source = 'BACKUP'")
    suspend fun activeBackup(): List<UploadJob>

    @Query("SELECT * FROM upload_jobs WHERE id = :id LIMIT 1")
    suspend fun byId(id: String): UploadJob?

    @Query("UPDATE upload_jobs SET phase = 'QUEUED' WHERE phase = 'UPLOADING'")
    suspend fun recoverInterruptedUploads()

    @Query("UPDATE upload_jobs SET phase = 'UPLOADED' WHERE phase = 'ATTACHING'")
    suspend fun recoverInterruptedAttachments()

    @Query("UPDATE upload_jobs SET phase = :phase, errorMessage = :errorMessage WHERE id = :id")
    suspend fun setPhase(id: String, phase: UploadPhase, errorMessage: String? = null)

    @Query("UPDATE upload_jobs SET bytesSent = :bytesSent, bytesTotal = :bytesTotal, phase = :phase WHERE id = :id")
    suspend fun setProgress(id: String, bytesSent: Long, bytesTotal: Long, phase: UploadPhase)

    @Query("UPDATE upload_jobs SET preparedFileId = :fileId, uploadUrl = :uploadUrl, phase = 'UPLOADING', bytesSent = 0 WHERE id = :id")
    suspend fun prepareUpload(id: String, fileId: String, uploadUrl: String)

    @Query("UPDATE upload_jobs SET preparedFileId = :fileId, phase = 'UPLOADED', bytesSent = bytesTotal WHERE id = :id")
    suspend fun markUploaded(id: String, fileId: String)

    @Query("UPDATE upload_jobs SET phase = 'PAUSED' WHERE source = 'BACKUP' AND phase IN ('QUEUED', 'UPLOADING', 'UPLOADED', 'ATTACHING')")
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
