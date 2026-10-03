package com.barkfluff.BarkCloud.data.upload

import com.barkfluff.BarkCloud.net.UploadSessionPart
import org.junit.Assert.assertEquals
import org.junit.Test

class UploadPartMathTest {

    @Test
    fun `part count and geometry for multi-part file`() {
        // 100 MiB, partSize 16 MiB → 7 частей, последняя 4 MiB
        val total = 100L * 1024 * 1024
        val partSize = 16L * 1024 * 1024

        assertEquals(7, UploadPartMath.partCount(total, partSize))
        assertEquals(0L, UploadPartMath.partOffset(1, partSize))
        assertEquals(16L * 1024 * 1024, UploadPartMath.partOffset(2, partSize))
        assertEquals(partSize, UploadPartMath.partLength(1, total, partSize))
        assertEquals(total - 6 * partSize, UploadPartMath.partLength(7, total, partSize))
    }

    @Test
    fun `single exact part file`() {
        assertEquals(1, UploadPartMath.partCount(16L * 1024 * 1024, 16L * 1024 * 1024))
        assertEquals(16L * 1024 * 1024, UploadPartMath.partLength(1, 16L * 1024 * 1024, 16L * 1024 * 1024))
    }

    @Test
    fun `missing parts respect etag and size match`() {
        val total = 50L * 1024 * 1024
        val partSize = 16L * 1024 * 1024 // 4 части: 16+16+16+2 MiB

        val uploaded = listOf(
            UploadSessionPart(1, 16L * 1024 * 1024, hasEtag = true),   // подтверждена
            UploadSessionPart(2, 16L * 1024 * 1024, hasEtag = false),  // без ETag — повторить
            UploadSessionPart(3, 15L * 1024 * 1024, hasEtag = true),   // размер не совпал — повторить
        )
        assertEquals(listOf(2, 3, 4), UploadPartMath.missingPartNumbers(uploaded, total, partSize))
        assertEquals(16L * 1024 * 1024, UploadPartMath.confirmedBytes(uploaded, total, partSize))
    }

    @Test
    fun `all parts confirmed means nothing missing`() {
        val total = 32L * 1024 * 1024
        val partSize = 16L * 1024 * 1024
        val uploaded = listOf(
            UploadSessionPart(1, partSize, hasEtag = true),
            UploadSessionPart(2, partSize, hasEtag = true),
        )
        assertEquals(emptyList<Int>(), UploadPartMath.missingPartNumbers(uploaded, total, partSize))
        assertEquals(total, UploadPartMath.confirmedBytes(uploaded, total, partSize))
    }

    @Test(expected = IllegalArgumentException::class)
    fun `empty file is rejected`() {
        UploadPartMath.partCount(0L, 16L * 1024 * 1024)
    }
}
