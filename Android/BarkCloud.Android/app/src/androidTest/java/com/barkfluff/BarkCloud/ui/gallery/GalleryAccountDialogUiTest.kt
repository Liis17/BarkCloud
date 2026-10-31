package com.barkfluff.BarkCloud.ui.gallery

import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.performClick
import com.barkfluff.BarkCloud.data.upload.UploadDestination
import com.barkfluff.BarkCloud.data.upload.UploadJob
import com.barkfluff.BarkCloud.data.upload.UploadPhase
import com.barkfluff.BarkCloud.data.upload.UploadSource
import com.barkfluff.BarkCloud.ui.upload.UploadQueueUiState
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test
import java.util.concurrent.atomic.AtomicInteger

class GalleryAccountDialogUiTest {
    @get:Rule val compose = createComposeRule()

    @Test
    fun uploadsTitleAndPhotoTilesOpenTheExistingQueue() {
        val opened = AtomicInteger()
        compose.setContent {
            MaterialTheme {
                GalleryAccountDialog(
                    displayName = "Test account",
                    username = "test",
                    avatarUrl = null,
                    storageInfo = null,
                    storageLoading = false,
                    uploadState = UploadQueueUiState(listOf(job())),
                    reclaimableBytes = 0,
                    onOpenUploads = { opened.incrementAndGet() },
                    onFreeDeviceSpace = {},
                    onOpenSettings = {},
                    onDismiss = {},
                )
            }
        }

        compose.onNodeWithContentDescription("Открыть очередь загрузок").assertIsDisplayed().performClick()
        compose.onNodeWithContentDescription("Загрузка photo.jpg, статус: В очереди").performClick()
        compose.runOnIdle { assertEquals(2, opened.get()) }
    }

    private fun job() = UploadJob(
        id = "queued-photo",
        source = UploadSource.BACKUP,
        stagedFilePath = null,
        mediaKey = null,
        mediaHash = null,
        fileName = "photo.jpg",
        mimeType = "image/jpeg",
        destination = UploadDestination.SYSTEM_BY_MEDIA_KIND,
        directoryId = null,
        albumId = null,
        phase = UploadPhase.QUEUED,
        idempotencyKey = null,
        sessionId = null,
        partSize = 0,
        preparedFileId = null,
        bytesTotal = 100,
        bytesSent = 0,
        errorMessage = null,
        createdAtMillis = 1,
        completedAtMillis = null,
    )
}
