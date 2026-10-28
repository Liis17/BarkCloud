package com.barkfluff.BarkCloud.data.persistence

import androidx.room.Entity
import androidx.room.Index
import androidx.room.PrimaryKey
import com.barkfluff.BarkCloud.data.cache.CacheCategory
import com.barkfluff.BarkCloud.data.cache.CacheScope
import com.barkfluff.BarkCloud.data.cache.FileCacheEntry
import com.barkfluff.BarkCloud.data.cache.FileCacheKey

@Entity(
    tableName = "file_cache_entries",
    indices = [
        Index(value = ["scopeServer", "accountId", "lastUsedAt"]),
        Index(value = ["lastUsedAt", "key"]),
    ],
)
data class FileCacheEntryEntity(
    @PrimaryKey val key: String,
    val scopeServer: String,
    val accountId: String,
    val fileId: String,
    val variant: String,
    val category: String,
    val sizeBytes: Long,
    val lastUsedAt: Long,
    val fileName: String,
) {
    fun toEntry(): FileCacheEntry = FileCacheEntry(
        key = FileCacheKey(
            scope = CacheScope(scopeServer, accountId),
            fileId = fileId,
            variant = variant,
            category = CacheCategory.valueOf(category),
        ),
        sizeBytes = sizeBytes,
        lastUsedAt = lastUsedAt,
        fileName = fileName,
    )

    companion object {
        fun from(entry: FileCacheEntry) = FileCacheEntryEntity(
            key = entry.key.storageKey,
            scopeServer = entry.key.scope.serverKey,
            accountId = entry.key.scope.accountId,
            fileId = entry.key.fileId,
            variant = entry.key.variant,
            category = entry.key.category.name,
            sizeBytes = entry.sizeBytes,
            lastUsedAt = entry.lastUsedAt,
            fileName = entry.fileName,
        )
    }
}
