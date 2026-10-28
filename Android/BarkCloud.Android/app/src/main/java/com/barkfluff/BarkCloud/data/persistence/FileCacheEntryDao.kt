package com.barkfluff.BarkCloud.data.persistence

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query

@Dao
interface FileCacheEntryDao {
    @Query("SELECT * FROM file_cache_entries WHERE `key` = :key LIMIT 1")
    suspend fun byKey(key: String): FileCacheEntryEntity?

    @Query("SELECT * FROM file_cache_entries")
    suspend fun all(): List<FileCacheEntryEntity>

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(entry: FileCacheEntryEntity)

    @Query("DELETE FROM file_cache_entries WHERE `key` = :key")
    suspend fun delete(key: String)

    @Query("DELETE FROM file_cache_entries")
    suspend fun deleteAll()
}
