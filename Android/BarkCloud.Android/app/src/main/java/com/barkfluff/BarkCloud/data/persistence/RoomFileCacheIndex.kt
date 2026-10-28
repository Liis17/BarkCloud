package com.barkfluff.BarkCloud.data.persistence

import com.barkfluff.BarkCloud.data.cache.FileCacheEntry
import com.barkfluff.BarkCloud.data.cache.FileCacheIndex
import com.barkfluff.BarkCloud.data.cache.CacheScope

class RoomFileCacheIndex(private val dao: FileCacheEntryDao) : FileCacheIndex {
    override suspend fun get(storageKey: String): FileCacheEntry? = dao.byKey(storageKey)?.toEntry()

    override suspend fun all(): List<FileCacheEntry> = dao.all().map(FileCacheEntryEntity::toEntry)

    override suspend fun totalSizeBytes(): Long = dao.totalSizeBytes()

    override suspend fun evictionCandidates(): List<FileCacheEntry> =
        dao.evictionCandidates().map(FileCacheEntryEntity::toEntry)

    override suspend fun entriesInScope(scope: CacheScope): List<FileCacheEntry> =
        dao.inScope(scope.serverKey, scope.accountId).map(FileCacheEntryEntity::toEntry)

    override suspend fun rebindScope(oldScope: CacheScope, newScope: CacheScope) =
        dao.rebindScope(oldScope, newScope)

    override suspend fun upsert(entry: FileCacheEntry) = dao.upsert(FileCacheEntryEntity.from(entry))

    override suspend fun delete(storageKey: String) = dao.delete(storageKey)

    override suspend fun clear() = dao.deleteAll()
}
