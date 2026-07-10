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

    @Query("DELETE FROM media_cloud_states WHERE mediaKey = :mediaKey")
    suspend fun delete(mediaKey: String)

    @Query("DELETE FROM media_cloud_states")
    suspend fun deleteAll()
}
