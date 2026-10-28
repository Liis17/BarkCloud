package com.barkfluff.BarkCloud.data.persistence

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import androidx.room.Transaction
import com.barkfluff.BarkCloud.data.cache.CacheScope

@Dao
interface FileCacheEntryDao {
    @Query("SELECT * FROM file_cache_entries WHERE `key` = :key LIMIT 1")
    suspend fun byKey(key: String): FileCacheEntryEntity?

    @Query("SELECT * FROM file_cache_entries")
    suspend fun all(): List<FileCacheEntryEntity>

    @Query("SELECT * FROM file_cache_entries WHERE scopeServer = :serverKey AND accountId = :accountId")
    suspend fun inScope(serverKey: String, accountId: String): List<FileCacheEntryEntity>

    @Query("SELECT COALESCE(SUM(sizeBytes), 0) FROM file_cache_entries")
    suspend fun totalSizeBytes(): Long

    @Query("SELECT * FROM file_cache_entries ORDER BY lastUsedAt ASC, `key` ASC LIMIT :limit OFFSET :offset")
    suspend fun oldest(limit: Int, offset: Int): List<FileCacheEntryEntity>

    @Query("SELECT * FROM file_cache_entries ORDER BY lastUsedAt DESC, `key` DESC LIMIT 1")
    suspend fun newest(): FileCacheEntryEntity?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(entry: FileCacheEntryEntity)

    @Query("DELETE FROM file_cache_entries WHERE `key` = :key")
    suspend fun delete(key: String)

    @Query("DELETE FROM file_cache_entries")
    suspend fun deleteAll()

    @Transaction
    suspend fun rebindScope(oldScope: CacheScope, newScope: CacheScope) {
        inScope(oldScope.serverKey, oldScope.accountId).forEach { entity ->
            val oldEntry = entity.toEntry()
            val newEntry = oldEntry.copy(key = oldEntry.key.copy(scope = newScope))
            val newKey = newEntry.key.storageKey
            if (byKey(newKey) == null) upsert(FileCacheEntryEntity.from(newEntry))
            delete(entity.key)
        }
    }
}
