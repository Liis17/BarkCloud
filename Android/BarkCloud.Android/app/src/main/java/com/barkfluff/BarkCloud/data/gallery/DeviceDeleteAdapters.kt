package com.barkfluff.BarkCloud.data.gallery

import com.barkfluff.BarkCloud.data.cloud.CloudCopy
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.data.persistence.MediaCloudStateDao
import com.barkfluff.BarkCloud.data.upload.UploadQueueStore
import com.barkfluff.BarkCloud.data.vault.VaultStore

/** Облако для выполнения удаления: состояние по хешу (UNKNOWN → null) и удаление файла. */
internal class CloudDeleteAdapter(private val cloud: CloudRepository) : DeleteCloudPort {
    override suspend fun copyState(hash: String): CloudCopy? =
        when (val copy = CloudCopy.of(cloud.checkFileHashesDetailed(listOf(hash))[hash])) {
            CloudCopy.Unknown -> null
            else -> copy
        }

    override suspend fun deleteFile(fileId: String) {
        cloud.deleteUserMedia(fileId)
    }
}

internal class DeviceCopyAdapter(
    private val queue: UploadQueueStore,
    private val mediaDao: MediaCloudStateDao,
) : DeviceCopyPort {
    override suspend fun cancelLiveJobs(mediaKey: String) = queue.cancelLiveForMediaKey(mediaKey)

    override suspend fun forget(mediaKey: String) = mediaDao.delete(mediaKey)
}

internal class VaultAdapter(private val store: VaultStore) : VaultPort {
    override fun fileIds(): Set<String> = store.items().map { it.fileId }.toSet()

    override fun remove(fileId: String) = store.remove(fileId)
}
