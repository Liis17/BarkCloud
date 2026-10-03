package com.barkfluff.BarkCloud.net

import okhttp3.MediaType.Companion.toMediaTypeOrNull
import okio.Buffer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

class UploadPartHttpTest {

    // MARK: PartAckParser

    @Test
    fun `valid receipt parses part number and size`() {
        assertEquals(3 to 16L * 1024 * 1024, PartAckParser.parse("""{"partNumber":3,"size":16777216}"""))
    }

    @Test
    fun `html page masquerading as receipt is rejected`() {
        assertNull(PartAckParser.parse("<html><body>502 Bad Gateway</body></html>"))
        assertNull(PartAckParser.parse("""{"error":"Части принимаются только как application/octet-stream."}"""))
    }

    @Test
    fun `receipt with wrong field types is rejected`() {
        assertNull(PartAckParser.parse("""{"partNumber":0,"size":16}"""))
        assertNull(PartAckParser.parse("""{"partNumber":3}"""))
        assertNull(PartAckParser.parse(""))

        // org.json коэрсит числа в строках — мягкий разбор допустим, сверка значений идёт дальше.
        assertEquals(3 to 16L, PartAckParser.parse("""{"partNumber":"3","size":16}"""))
    }

    // MARK: UploadPartHttpException

    @Test
    fun `retry classification of http errors`() {
        assertTrue(UploadPartHttpException(500, "").retryable)
        assertTrue(UploadPartHttpException(503, "").retryable)
        assertTrue(UploadPartHttpException(408, "").retryable)
        assertTrue(UploadPartHttpException(429, "").retryable)
        assertTrue(UploadPartHttpException(401, "").retryable) // протухший token лечится resume
        assertFalse(UploadPartHttpException(409, "").retryable)
        assertFalse(UploadPartHttpException(413, "").retryable)
    }

    // MARK: FileSegmentRequestBody

    @Test
    fun `request body streams exact file window`() {
        val file = File.createTempFile("segment", ".bin")
        try {
            val data = ByteArray(10_000) { (it % 251).toByte() }
            file.writeBytes(data)

            val body = FileSegmentRequestBody(file, offset = 3_000, length = 4_000) {}
            assertEquals(4_000L, body.contentLength())
            assertEquals("application/octet-stream", body.contentType().toString())

            val progress = mutableListOf<Long>()
            val bodyWithProgress = FileSegmentRequestBody(file, offset = 3_000, length = 4_000) { progress.add(it) }
            val buffer = Buffer()
            bodyWithProgress.writeTo(buffer)
            assertEquals(4_000L, buffer.size)
            assertEquals(data.copyOfRange(3_000, 7_000).toList(), buffer.readByteArray().toList())
            assertEquals(4_000L, progress.last())
        } finally {
            file.delete()
        }
    }

    @Test
    fun `request body reports sent bytes in order`() {
        val file = File.createTempFile("segment-progress", ".bin")
        try {
            file.writeBytes(ByteArray(300_000) { 7 })
            val progress = mutableListOf<Long>()
            FileSegmentRequestBody(file, 0, 300_000) { progress.add(it) }.writeTo(Buffer())
            assertEquals(progress, progress.sorted())
            assertEquals(300_000L, progress.last())
        } finally {
            file.delete()
        }
    }
}
