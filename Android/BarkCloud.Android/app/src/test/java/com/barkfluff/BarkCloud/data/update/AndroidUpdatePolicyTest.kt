package com.barkfluff.BarkCloud.data.update

import java.io.File
import java.time.LocalDate
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

class AndroidUpdatePolicyTest {
    @get:Rule val temporaryFolder = TemporaryFolder()

    @Test
    fun startupPromptIsOncePerVersionAndDayAndDoesNotRepeatAfterClockRollback() {
        val release = metadata("v1.0.5")
        val firstDay = LocalDate.of(2026, 10, 31)
        assertTrue(AndroidUpdatePolicy.shouldPrompt(release, 2, firstDay, promptedDay = null))
        assertFalse(AndroidUpdatePolicy.shouldPrompt(release, 2, firstDay, promptedDay = firstDay))
        assertFalse(AndroidUpdatePolicy.shouldPrompt(release, 2, firstDay.minusDays(1), promptedDay = firstDay))
        assertTrue(AndroidUpdatePolicy.shouldPrompt(release, 2, firstDay.plusDays(1), promptedDay = firstDay))
        assertFalse(AndroidUpdatePolicy.shouldPrompt(release, release.versionCode, firstDay.plusDays(1), promptedDay = null))
    }

    @Test
    fun successfulCheckInvalidatesRevokedOrChangedApkAndTtlRespectsRollback() {
        val ready = metadata("v1.0.5")
        assertTrue(AndroidUpdatePolicy.shouldInvalidateReadyFile(ready, metadata("v1.0.6")))
        assertTrue(AndroidUpdatePolicy.shouldInvalidateReadyFile(ready, ready.copy(sha256 = "cd".repeat(32))))
        assertFalse(AndroidUpdatePolicy.shouldInvalidateReadyFile(ready, ready.copy(notes = "Updated notes")))
        assertTrue(AndroidUpdatePolicy.shouldDeleteForTtl(readyAt + AndroidUpdatePolicy.DOWNLOAD_TTL_MILLIS, readyAt))
        assertFalse(AndroidUpdatePolicy.shouldDeleteForTtl(readyAt - 1, readyAt))
    }

    @Test
    fun cleanupOnlyRecognizesFilesInsideTheAppUpdatesDirectory() {
        val root = temporaryFolder.newFolder("external")
        val updates = File(root, "updates").apply { mkdirs() }
        val owned = File(updates, "update.apk").apply { writeText("owned") }
        val outside = File(root, "other.apk").apply { writeText("other") }

        assertTrue(AndroidUpdatePolicy.isOwnedUpdatePath(owned, updates))
        assertFalse(AndroidUpdatePolicy.isOwnedUpdatePath(outside, updates))
        assertFalse(AndroidUpdatePolicy.isOwnedUpdatePath(updates, updates))
    }

    private fun metadata(tag: String) = AndroidReleaseMetadata(
        tag = tag,
        versionName = tag.removePrefix("v"),
        versionCode = AndroidReleaseParser.versionCode(tag),
        apkFileName = "BarkCloud.Android-$tag.apk",
        apkSize = 128,
        sha256 = "ab".repeat(32),
        notes = "",
    )

    private companion object {
        const val readyAt = 1_000_000L
    }
}
