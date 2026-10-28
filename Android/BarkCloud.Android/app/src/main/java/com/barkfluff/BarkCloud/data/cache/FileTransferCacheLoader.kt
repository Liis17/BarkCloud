package com.barkfluff.BarkCloud.data.cache

import com.barkfluff.BarkCloud.net.FileTransferService
import java.io.File
import java.io.IOException
import java.util.UUID

class FileTransferCacheLoader(
    private val transfer: FileTransferService,
    private val temporaryDirectory: File,
) : FileCacheLoader {
    override suspend fun load(request: FileCacheLoadRequest, destination: File) {
        val transferTempName = "barkcloud-cache-${UUID.randomUUID()}.tmp"
        val transferTemp = File(temporaryDirectory, transferTempName)
        var downloaded: File? = null
        try {
            val url = request.sourceUrl ?: run {
                if (request.key.category != CacheCategory.ORIGINAL) {
                    throw IOException("Для превью требуется URL загрузки")
                }
                transfer.tempDownloadUrls(listOf(request.key.fileId))[request.key.fileId]
                    ?: throw IOException("Ссылка загрузки не найдена")
            }
            val completedDownload = transfer.download(url, transferTempName)
            downloaded = completedDownload
            completedDownload.copyTo(destination, overwrite = true)
            if (destination.length() <= 0L) throw IOException("Загружен пустой файл")
        } finally {
            downloaded?.delete()
            transferTemp.delete()
        }
    }
}
