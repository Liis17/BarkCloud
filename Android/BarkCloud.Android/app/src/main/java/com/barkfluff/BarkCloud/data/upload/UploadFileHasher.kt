package com.barkfluff.BarkCloud.data.upload

import java.io.File
import java.security.MessageDigest

/** Потоковый SHA-256 staged-файла блоками 4 MiB: hex (lower) + фактический размер. */
object UploadFileHasher {

    fun hashAndSize(file: File): Pair<String, Long>? = runCatching {
        val digest = MessageDigest.getInstance("SHA-256")
        var size = 0L
        file.inputStream().use { input ->
            val buffer = ByteArray(4 * 1024 * 1024)
            while (true) {
                val read = input.read(buffer)
                if (read < 0) break
                digest.update(buffer, 0, read)
                size += read.toLong()
            }
        }
        digest.digest().joinToString("") { "%02x".format(it) } to size
    }.getOrNull()
}
