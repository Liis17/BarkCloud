package com.barkfluff.BarkCloud.data.update

import java.io.File
import java.security.MessageDigest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class ApkIntegrityTest {
    @get:Rule val temporaryFolder = TemporaryFolder()

    @Test
    fun verifiesExactFileSizeAndSha256() {
        val file = temporaryFolder.newFile("release.apk").apply { writeBytes("signed apk bytes".toByteArray()) }
        val hash = ApkIntegrity.sha256(file)

        assertEquals(expectedSha256(file), hash)
        ApkIntegrity.verifyFile(file, file.length(), hash)
        expectThrows<IllegalArgumentException> { ApkIntegrity.verifyFile(file, file.length() + 1, hash) }
        expectThrows<IllegalArgumentException> { ApkIntegrity.verifyFile(file, file.length(), "00".repeat(32)) }
    }

    @Test
    fun acceptsSameSignerAndForwardSigningLineageOnly() {
        val old = ApkSignerSet(setOf("old"), setOf("old"), hasMultipleSigners = false)
        val same = ApkSignerSet(setOf("old"), setOf("old"), hasMultipleSigners = false)
        val rotated = ApkSignerSet(setOf("new"), setOf("old", "new"), hasMultipleSigners = false)
        val olderCandidate = ApkSignerSet(setOf("old"), setOf("old"), hasMultipleSigners = false)

        assertTrue(ApkIntegrity.signingCompatible(same, old))
        assertTrue(ApkIntegrity.signingCompatible(rotated, old))
        assertFalse(ApkIntegrity.signingCompatible(olderCandidate, rotated))
        assertFalse(ApkIntegrity.signingCompatible(rotated, old.copy(hasMultipleSigners = true)))
    }

    @Test
    fun requiresExactSignerSetForPackagesWithMultipleSigners() {
        val two = ApkSignerSet(setOf("a", "b"), emptySet(), hasMultipleSigners = true)
        val same = two.copy()
        val one = ApkSignerSet(setOf("a"), emptySet(), hasMultipleSigners = true)

        assertTrue(ApkIntegrity.signingCompatible(two, same))
        assertFalse(ApkIntegrity.signingCompatible(two, one))
    }

    private fun expectedSha256(file: File): String = MessageDigest.getInstance("SHA-256")
        .digest(file.readBytes())
        .joinToString("") { "%02x".format(it) }
}
