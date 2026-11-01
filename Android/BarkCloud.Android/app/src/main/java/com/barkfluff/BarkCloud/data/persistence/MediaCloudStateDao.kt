package com.barkfluff.BarkCloud.data.persistence

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import com.barkfluff.BarkCloud.data.gallery.MediaCloudState
import kotlinx.coroutines.flow.Flow

@Dao
interface MediaCloudStateDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(state: MediaCloudState)

    @Query("SELECT * FROM media_cloud_states")
    fun observeAll(): Flow<List<MediaCloudState>>

    @Query("SELECT * FROM media_cloud_states")
    suspend fun all(): List<MediaCloudState>

    @Query("SELECT * FROM media_cloud_states WHERE mediaKey = :mediaKey LIMIT 1")
    suspend fun byKey(mediaKey: String): MediaCloudState?

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertIfAbsent(state: MediaCloudState): Long

    /**
     * Присутствие в облаке подтверждено. Условия защищают от гонок: строка должна описывать
     * тот же снимок файла, а идущая загрузка (QUEUED/UPLOADING) не перезаписывается.
     */
    @Query(
        "UPDATE media_cloud_states SET status = 'IN_CLOUD', hash = :hash, cloudFileId = :cloudFileId " +
            "WHERE mediaKey = :mediaKey AND dateModifiedSeconds = :dateModifiedSeconds AND sizeBytes = :sizeBytes " +
            "AND status NOT IN ('QUEUED', 'UPLOADING')",
    )
    suspend fun markInCloud(mediaKey: String, dateModifiedSeconds: Long, sizeBytes: Long, hash: String, cloudFileId: String?): Int

    /** Облачной копии нет. Не понижает IN_CLOUD, чтобы поздний ответ не затёр завершённую загрузку. */
    @Query(
        "UPDATE media_cloud_states SET status = 'NOT_IN_CLOUD', hash = :hash, cloudFileId = NULL " +
            "WHERE mediaKey = :mediaKey AND dateModifiedSeconds = :dateModifiedSeconds AND sizeBytes = :sizeBytes " +
            "AND status NOT IN ('QUEUED', 'UPLOADING', 'IN_CLOUD')",
    )
    suspend fun markAbsent(mediaKey: String, dateModifiedSeconds: Long, sizeBytes: Long, hash: String): Int

    @Query(
        "UPDATE media_cloud_states SET hash = :hash WHERE mediaKey = :mediaKey " +
            "AND dateModifiedSeconds = :dateModifiedSeconds AND sizeBytes = :sizeBytes",
    )
    suspend fun setHash(mediaKey: String, dateModifiedSeconds: Long, sizeBytes: Long, hash: String): Int

    /** Файл удалён из облака: строки, ссылавшиеся на него, больше не считаются присутствующими. */
    @Query("UPDATE media_cloud_states SET status = 'NOT_IN_CLOUD', cloudFileId = NULL WHERE cloudFileId = :cloudFileId")
    suspend fun markCloudFileRemoved(cloudFileId: String): Int

    @Query("DELETE FROM media_cloud_states WHERE mediaKey = :mediaKey")
    suspend fun delete(mediaKey: String)

    @Query("DELETE FROM media_cloud_states")
    suspend fun deleteAll()
}
