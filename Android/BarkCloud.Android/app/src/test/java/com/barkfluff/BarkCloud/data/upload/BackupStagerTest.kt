package com.barkfluff.BarkCloud.data.upload

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.File
import java.nio.file.Files
import java.security.MessageDigest

class BackupStagerTest {
    @Test
    fun stalePartIsRecreatedAndOnlyTheHashedFinalFileRemains() = runTest {
        val directory = Files.createTempDirectory("backup-stage").toFile()
        val part = File(directory, "backup-job.part")
        val destination = File(directory, "backup-job.stage")
        part.writeText("interrupted old copy")
        val bytes = ByteArray(128 * 1024) { (it % 251).toByte() }

        val (hash, size) = BackupStager.stage(
            part = part,
            destination = destination,
            expectedSize = bytes.size.toLong(),
            openSource = { ByteArrayInputStream(bytes) },
            verifySource = {},
            ensureActive = {},
        )

        assertArrayEquals(bytes, destination.readBytes())
        assertFalse(part.exists())
        assertTrue(destination.exists())
        assertEquals(bytes.size.toLong(), size)
        assertEquals(sha256(bytes), hash)
        directory.deleteRecursively()
    }

    @Test
    fun cancellationWhileCopyingDeletesOnlyTheOwnPartialAndFinalFiles() = runTest {
        val directory = Files.createTempDirectory("backup-cancel").toFile()
        val part = File(directory, "backup-job.part")
        val destination = File(directory, "backup-job.stage")
        val bytes = ByteArray(8 * 1024 * 1024) { 7 }
        var phaseChecks = 0

        assertThrows(CancellationException::class.java) {
            kotlinx.coroutines.runBlocking {
                BackupStager.stage(
                    part = part,
                    destination = destination,
                    expectedSize = bytes.size.toLong(),
                    openSource = { ByteArrayInputStream(bytes) },
                    verifySource = {},
                    ensureActive = {
                        phaseChecks++
                        if (phaseChecks == 1) throw CancellationException("worker stopped")
                    },
                )
            }
        }

        assertTrue(phaseChecks > 0)
        assertFalse(part.exists())
        assertFalse(destination.exists())
        directory.deleteRecursively()
    }

    private fun sha256(bytes: ByteArray): String = MessageDigest.getInstance("SHA-256")
        .digest(bytes)
        .joinToString("") { "%02x".format(it) }
}
