package com.barkfluff.BarkCloud.data.gallery

import com.barkfluff.BarkCloud.data.persistence.MediaCloudStateDao
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow


/** In-memory стенд для таблицы media_cloud_states с теми же условиями, что у SQL-запросов DAO. */
internal class FakeMediaCloudStateDao : MediaCloudStateDao {
    val rows = HashMap<String, MediaCloudState>()
    private val flow = MutableStateFlow<List<MediaCloudState>>(emptyList())

    override suspend fun upsert(state: MediaCloudState) {
        rows[state.mediaKey] = state
    }

    override fun observeAll(): Flow<List<MediaCloudState>> = flow

    override suspend fun all(): List<MediaCloudState> = rows.values.toList()

    override suspend fun byKey(mediaKey: String): MediaCloudState? = rows[mediaKey]

    override suspend fun insertIfAbsent(state: MediaCloudState): Long {
        if (rows.containsKey(state.mediaKey)) return -1L
        rows[state.mediaKey] = state
        return 1L
    }

    override suspend fun markInCloud(
        mediaKey: String,
        dateModifiedSeconds: Long,
        sizeBytes: Long,
        hash: String,
        cloudFileId: String?,
    ): Int {
        val row = rows[mediaKey] ?: return 0
        if (row.dateModifiedSeconds != dateModifiedSeconds || row.sizeBytes != sizeBytes) return 0
        if (row.status == MediaCloudStatus.QUEUED || row.status == MediaCloudStatus.UPLOADING) return 0
        rows[mediaKey] = row.copy(status = MediaCloudStatus.IN_CLOUD, hash = hash, cloudFileId = cloudFileId)
        return 1
    }

    override suspend fun markAbsent(mediaKey: String, dateModifiedSeconds: Long, sizeBytes: Long, hash: String): Int {
        val row = rows[mediaKey] ?: return 0
        if (row.dateModifiedSeconds != dateModifiedSeconds || row.sizeBytes != sizeBytes) return 0
        if (row.status in setOf(MediaCloudStatus.QUEUED, MediaCloudStatus.UPLOADING, MediaCloudStatus.IN_CLOUD)) return 0
        rows[mediaKey] = row.copy(status = MediaCloudStatus.NOT_IN_CLOUD, hash = hash, cloudFileId = null)
        return 1
    }

    override suspend fun setHash(mediaKey: String, dateModifiedSeconds: Long, sizeBytes: Long, hash: String): Int {
        val row = rows[mediaKey] ?: return 0
        if (row.dateModifiedSeconds != dateModifiedSeconds || row.sizeBytes != sizeBytes) return 0
        rows[mediaKey] = row.copy(hash = hash)
        return 1
    }

    override suspend fun markCloudFileRemoved(cloudFileId: String): Int = 0

    override suspend fun delete(mediaKey: String) {
        rows.remove(mediaKey)
    }

    override suspend fun deleteAll() {
        rows.clear()
    }
}
