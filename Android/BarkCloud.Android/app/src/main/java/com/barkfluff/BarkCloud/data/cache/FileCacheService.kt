package com.barkfluff.BarkCloud.data.cache

import android.content.Context
import com.barkfluff.BarkCloud.net.FileTransferService
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File

class FileCacheService(
    context: Context,
    private val transfer: FileTransferService,
    private val settings: FileCacheSettings,
) {
    private val originalsDir = File(context.cacheDir, "BarkCloudFiles/originals")

    suspend fun loadOriginal(fileId: String, fileName: String): File = withContext(Dispatchers.IO) {
        originalsDir.mkdirs()
        val cached = originalFile(fileId, fileName)
        if (cached.exists() && cached.length() > 0) {
            cached.setLastModified(System.currentTimeMillis())
            return@withContext cached
        }

        val url = transfer.tempDownloadUrls(listOf(fileId))[fileId] ?: error("no download url")
        val downloaded = transfer.download(url, "${safeName(fileId)}-${fileName.ifEmpty { "file" }}")
        if (cached.exists()) cached.delete()
        downloaded.copyTo(cached, overwrite = true)
        downloaded.delete()
        cached.setLastModified(System.currentTimeMillis())
        enforceSizeLimit()
        cached
    }

    suspend fun totalSize(): Long = withContext(Dispatchers.IO) {
        cacheFiles().sumOf { it.length() }
    }

    suspend fun entryCount(): Int = withContext(Dispatchers.IO) {
        cacheFiles().size
    }

    suspend fun clearAll() = withContext(Dispatchers.IO) {
        originalsDir.deleteRecursively()
        originalsDir.mkdirs()
    }

    suspend fun clearStale(): Int = withContext(Dispatchers.IO) {
        val age = settings.staleMaxAgeMillis
        if (age == FileCacheSettings.NEVER) return@withContext 0
        val cutoff = System.currentTimeMillis() - age
        cacheFiles()
            .filter { it.lastModified() < cutoff }
            .count { it.delete() }
    }

    suspend fun runStartupSweepIfNeeded() = withContext(Dispatchers.IO) {
        val now = System.currentTimeMillis()
        if (now - settings.lastSweepAtMillis < FileCacheSettings.DAY_MILLIS) return@withContext
        val age = settings.staleMaxAgeMillis
        if (age != FileCacheSettings.NEVER) {
            val cutoff = now - age
            cacheFiles().filter { it.lastModified() < cutoff }.forEach { it.delete() }
        }
        enforceSizeLimit()
        settings.lastSweepAtMillis = now
    }

    suspend fun enforceSizeLimit() = withContext(Dispatchers.IO) {
        val max = settings.maxCacheBytes
        var files = cacheFiles().sortedBy { it.lastModified() }
        var total = files.sumOf { it.length() }
        for (file in files) {
            if (total <= max) break
            val size = file.length()
            if (file.delete()) total -= size
        }
    }

    private fun originalFile(fileId: String, fileName: String): File {
        val ext = fileName.substringAfterLast('.', missingDelimiterValue = "")
            .takeIf { it.length in 1..12 }
            ?.let { ".$it" }
            .orEmpty()
        return File(originalsDir, "${safeName(fileId)}$ext")
    }

    private fun cacheFiles(): List<File> =
        originalsDir.walkTopDown()
            .filter { it.isFile }
            .toList()

    private fun safeName(value: String): String =
        value.map { if (it.isLetterOrDigit() || it == '-' || it == '_') it else '_' }
            .joinToString("")
            .ifBlank { "file" }
}
