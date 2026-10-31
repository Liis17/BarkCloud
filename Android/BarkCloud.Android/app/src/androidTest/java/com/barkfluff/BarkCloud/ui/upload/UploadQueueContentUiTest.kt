package com.barkfluff.BarkCloud.ui.upload

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.size
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.SemanticsMatcher
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.getBoundsInRoot
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.compose.ui.unit.dp
import com.barkfluff.BarkCloud.data.upload.UploadDestination
import com.barkfluff.BarkCloud.data.upload.UploadJob
import com.barkfluff.BarkCloud.data.upload.UploadPhase
import com.barkfluff.BarkCloud.data.upload.UploadSource
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import java.util.concurrent.atomic.AtomicReference

class UploadQueueContentUiTest {
    @get:Rule val compose = createComposeRule()

    @Test
    fun queueUsesThreeColumns() {
        compose.setContent {
            MaterialTheme {
                Box(Modifier.size(360.dp, 640.dp)) {
                    UploadQueueContent(
                        state = UploadQueueUiState((0..3).map { job(UploadPhase.QUEUED, "job-$it", it.toLong()) }),
                        onNavigateUp = {},
                        onRetry = {},
                        onCancel = {},
                        modifier = Modifier.fillMaxSize(),
                    )
                }
            }
        }

        val bounds = (0..3).map { index ->
            compose.onNodeWithContentDescription("Загрузка job-$index, статус: В очереди").getBoundsInRoot()
        }
        assertEquals(bounds[0].top.value, bounds[1].top.value, 1f)
        assertEquals(bounds[0].top.value, bounds[2].top.value, 1f)
        assertTrue(bounds[0].left < bounds[1].left && bounds[1].left < bounds[2].left)
        assertEquals(bounds[0].left.value, bounds[3].left.value, 1f)
        assertTrue(bounds[3].top > bounds[0].top)

    }

    @Test
    fun emptyQueueDisplaysItsMessage() {
        compose.setContent {
            MaterialTheme {
                UploadQueueContent(
                    state = UploadQueueUiState(),
                    onNavigateUp = {},
                    onRetry = {},
                    onCancel = {},
                )
            }
        }

        compose.onNodeWithText("Нет ожидающих загрузок").assertIsDisplayed()
    }

    @Test
    fun onlyExecutingJobsShowProgress() {
        val state = mutableStateOf(UploadQueueUiState(listOf(job(UploadPhase.QUEUED, "queued", 1))))
        compose.setContent {
            MaterialTheme {
                UploadQueueContent(
                    state = state.value,
                    onNavigateUp = {},
                    onRetry = {},
                    onCancel = {},
                )
            }
        }
        val progressMatcher = SemanticsMatcher.keyIsDefined(SemanticsProperties.ProgressBarRangeInfo)

        assertEquals(0, compose.onAllNodes(progressMatcher).fetchSemanticsNodes().size)
        compose.runOnIdle {
            state.value = UploadQueueUiState(listOf(job(UploadPhase.UPLOADING, "uploading", 1)))
        }
        compose.waitForIdle()
        assertEquals(1, compose.onAllNodes(progressMatcher).fetchSemanticsNodes().size)
    }

    @Test
    fun detailSheetShowsFullInformationAndKeepsRetryCancelActions() {
        val retryId = AtomicReference<String?>()
        val cancelId = AtomicReference<String?>()
        val failed = job(UploadPhase.FAILED, "failed-id", 1).copy(
            fileName = "very-long-file-name-photo.jpg",
            errorMessage = "Сервер не принял файл",
        )
        val queued = job(UploadPhase.QUEUED, "queued-id", 2)
        compose.setContent {
            MaterialTheme {
                UploadQueueContent(
                    state = UploadQueueUiState(listOf(queued, failed)),
                    onNavigateUp = {},
                    onRetry = { retryId.set(it) },
                    onCancel = { cancelId.set(it) },
                )
            }
        }

        compose.onNodeWithContentDescription("Загрузка very-long-file-name-photo.jpg, статус: Ошибка").performClick()
        compose.onNodeWithText("very-long-file-name-photo.jpg").assertIsDisplayed()
        compose.onNodeWithText("Сервер не принял файл").assertIsDisplayed()
        compose.onNodeWithText("Повторить").performClick()
        compose.runOnIdle { assertEquals("failed-id", retryId.get()) }

        compose.onNodeWithContentDescription("Загрузка photo.jpg, статус: В очереди").performClick()
        compose.onNodeWithText("Отмена").performClick()
        compose.runOnIdle { assertEquals("queued-id", cancelId.get()) }
    }

    @Test
    fun changingVisibleJobPhaseKeepsTheReadingRowInPlace() {
        val initial = (0..39).map { index ->
            job(UploadPhase.QUEUED, "job-${index.toString().padStart(2, '0')}", index.toLong())
        }
        val state = mutableStateOf(UploadQueueUiState(initial))
        compose.setContent {
            MaterialTheme {
                Box(Modifier.size(360.dp, 640.dp)) {
                    UploadQueueContent(
                        state = state.value,
                        onNavigateUp = {},
                        onRetry = {},
                        onCancel = {},
                        modifier = Modifier.fillMaxSize(),
                    )
                }
            }
        }

        compose.onNodeWithContentDescription("Загрузка job-15, статус: В очереди").performScrollTo()
        val anchorBefore = compose.onNodeWithContentDescription("Загрузка job-16, статус: В очереди").getBoundsInRoot()
        compose.runOnIdle {
            state.value = UploadQueueUiState(
                initial.map { if (it.id == "job-15") it.copy(phase = UploadPhase.UPLOADING) else it },
            )
        }
        compose.waitForIdle()

        val anchorAfter = compose.onNodeWithContentDescription("Загрузка job-16, статус: В очереди").getBoundsInRoot()
        assertEquals(anchorBefore.top.value, anchorAfter.top.value, 1f)
    }

    private fun job(phase: UploadPhase, id: String, createdAt: Long) = UploadJob(
        id = id,
        source = UploadSource.BACKUP,
        stagedFilePath = null,
        mediaKey = null,
        mediaHash = null,
        fileName = if (id == "queued-id") "photo.jpg" else id,
        mimeType = "image/jpeg",
        destination = UploadDestination.SYSTEM_BY_MEDIA_KIND,
        directoryId = null,
        albumId = null,
        phase = phase,
        idempotencyKey = null,
        sessionId = null,
        partSize = 0,
        preparedFileId = null,
        bytesTotal = 100,
        bytesSent = 35,
        errorMessage = null,
        createdAtMillis = createdAt,
        completedAtMillis = null,
    )
}
