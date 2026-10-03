package com.barkfluff.BarkCloud.data.upload

import org.junit.Assert.assertEquals
import org.junit.Test
import java.io.File

class UploadFileHasherTest {

    @Test
    fun `hash and size of known content`() {
        val file = File.createTempFile("hash", ".bin")
        try {
            val data = ByteArray(5 * 1024 * 1024 + 123) { (it % 256).toByte() } // больше одного 4 MiB чанка
            file.writeBytes(data)

            val (hash, size) = requireNotNull(UploadFileHasher.hashAndSize(file))
            assertEquals(data.size.toLong(), size)
            // SHA-256 от "зацикленных" байтов 0..255 совпадает с эталоном, вычисленным независимо.
            val expected = java.security.MessageDigest.getInstance("SHA-256").digest(data)
                .joinToString("") { "%02x".format(it) }
            assertEquals(expected, hash)
            assertEquals(64, hash.length)
            assertEquals(true, hash.all { it in '0'..'9' || it in 'a'..'f' })
        } finally {
            file.delete()
        }
    }

    @Test
    fun `missing file returns null`() {
        assertEquals(null, UploadFileHasher.hashAndSize(File("/nonexistent/path/file.bin")))
    }
}
