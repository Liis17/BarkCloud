package com.barkfluff.BarkCloud.data.gallery

import android.content.Context
import android.net.Uri
import java.security.MessageDigest

/**
 * Потоковый SHA-256 содержимого [uri] (hex в нижнем регистре). Хешируется ровно тот
 * поток, что заливается при upload (`ContentResolver.openInputStream`), поэтому хеш
 * совпадает с серверным — `CheckFileHashes` корректно определяет наличие файла в облаке.
 */
object MediaHasher {

    fun sha256(context: Context, uri: Uri): String? = runCatching {
        context.contentResolver.openInputStream(uri)?.use { input ->
            val md = MessageDigest.getInstance("SHA-256")
            val buffer = ByteArray(64 * 1024)
            var read = input.read(buffer)
            while (read >= 0) {
                md.update(buffer, 0, read)
                read = input.read(buffer)
            }
            md.digest().joinToString("") { "%02x".format(it) }
        }
    }.getOrNull()
}
