package com.barkfluff.BarkCloud.ui.gallery

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.getBoundsInRoot
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.performClick
import androidx.compose.ui.unit.dp
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

    @Test
    fun horizontalUploadReorderingAndCompletionKeepVisiblePhotoInPlace() {
        val initial = (0..29).map { index ->
            job().copy(id = "row-$index", fileName = "row-$index.jpg", createdAtMillis = index.toLong())
        }
        val state = mutableStateOf(UploadQueueUiState(initial))
        compose.setContent {
            MaterialTheme {
                Box(Modifier.width(360.dp)) {
                    val listState = rememberLazyListState(initialFirstVisibleItemIndex = 10)
                    GalleryAccountUploadRow(
                        jobs = state.value.pendingJobs,
                        onOpenUploads = {},
                        listState = listState,
                    )
                }
            }
        }
        compose.waitForIdle()

        val anchorLabel = "Загрузка row-13.jpg, статус: В очереди"
        val anchorBefore = compose.onNodeWithContentDescription(anchorLabel).getBoundsInRoot()
        compose.runOnIdle {
            state.value = UploadQueueUiState(
                initial.map { job ->
                    when (job.id) {
                        "row-10" -> job.copy(phase = UploadPhase.FAILED)
                        "row-11" -> job.copy(phase = UploadPhase.UPLOADING)
                        "row-12" -> job.copy(phase = UploadPhase.COMPLETED)
                        else -> job
                    }
                },
            )
        }
        compose.waitForIdle()

        val anchorAfter = compose.onNodeWithContentDescription(anchorLabel).getBoundsInRoot()
        assertEquals(anchorBefore.left.value, anchorAfter.left.value, 1f)
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
