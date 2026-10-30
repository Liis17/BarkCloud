package com.barkfluff.BarkCloud.data.cache

import java.io.File
import java.io.IOException
import java.security.MessageDigest
import java.util.UUID

internal object LocalShareFileCache {
    const val DIRECTORY_NAME = "shared-files"
    const val RETENTION_MILLIS = 30L * 24 * 60 * 60 * 1000

    @Synchronized
    fun prepare(
        cacheDir: File,
        source: File,
        nowMillis: Long = System.currentTimeMillis(),
        displayName: String = source.name,
        copyFile: (File, File) -> Unit = { input, output -> input.copyTo(output, overwrite = true) },
    ): File {
        if (!source.isFile) throw IOException("Share source is no longer available")
        val directory = File(cacheDir, DIRECTORY_NAME)
        if (!directory.isDirectory && !directory.mkdirs() && !directory.isDirectory) {
            throw IOException("Could not create share cache directory")
        }
        removeExpired(directory, nowMillis)

        val sourceLength = source.length()
        val sourceLastModified = source.lastModified()
        val targetName = displayName.replace('\\', '/').substringAfterLast('/')
            .takeIf { it.isNotBlank() && it != "." && it != ".." }
            ?: source.name
        val key = fingerprint("${source.canonicalPath}\u0000$sourceLastModified\u0000$sourceLength\u0000$targetName")
        val entry = File(directory, "share-$key")
        if (!entry.isDirectory && !entry.mkdirs() && !entry.isDirectory) {
            throw IOException("Could not create share cache entry")
        }
        val target = File(entry, targetName)
        if (target.isFile && target.length() == sourceLength) {
            entry.setLastModified(nowMillis)
            target.setLastModified(nowMillis)
            return target
        }

        val partial = File(entry, "${UUID.randomUUID()}.partial")
        var complete = false
        try {
            copyFile(source, partial)
            if (partial.length() != sourceLength ||
                source.length() != sourceLength ||
                source.lastModified() != sourceLastModified
            ) {
                throw IOException("Share source changed while it was being copied")
            }
            if (!partial.renameTo(target)) {
                if (!target.isFile || target.length() != sourceLength) {
                    throw IOException("Could not finish share cache copy")
                }
                partial.delete()
            }
            target.setLastModified(nowMillis)
            entry.setLastModified(nowMillis)
            complete = true
            return target
        } finally {
            if (!complete) {
                partial.delete()
                if (entry.listFiles().orEmpty().isEmpty()) entry.delete()
            }
        }
    }

    @Synchronized
    fun clear(cacheDir: File) {
        File(cacheDir, DIRECTORY_NAME).deleteRecursively()
    }

    private fun removeExpired(directory: File, nowMillis: Long) {
        val cutoff = nowMillis - RETENTION_MILLIS
        directory.listFiles()
            ?.filter { it.lastModified() < cutoff }
            ?.forEach { it.deleteRecursively() }
    }

    private fun fingerprint(value: String): String =
        MessageDigest.getInstance("SHA-256")
            .digest(value.toByteArray(Charsets.UTF_8))
            .joinToString("") { (it.toInt() and 0xff).toString(16).padStart(2, '0') }
}
