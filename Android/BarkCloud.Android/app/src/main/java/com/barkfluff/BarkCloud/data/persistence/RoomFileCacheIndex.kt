package com.barkfluff.BarkCloud.data.persistence

import com.barkfluff.BarkCloud.data.cache.FileCacheEntry
import com.barkfluff.BarkCloud.data.cache.FileCacheIndex

class RoomFileCacheIndex(private val dao: FileCacheEntryDao) : FileCacheIndex {
    override suspend fun get(storageKey: String): FileCacheEntry? = dao.byKey(storageKey)?.toEntry()

    override suspend fun all(): List<FileCacheEntry> = dao.all().map(FileCacheEntryEntity::toEntry)

    override suspend fun upsert(entry: FileCacheEntry) = dao.upsert(FileCacheEntryEntity.from(entry))

    override suspend fun delete(storageKey: String) = dao.delete(storageKey)

    override suspend fun clear() = dao.deleteAll()
}
