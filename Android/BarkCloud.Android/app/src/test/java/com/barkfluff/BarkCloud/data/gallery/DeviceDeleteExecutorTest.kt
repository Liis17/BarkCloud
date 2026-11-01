package com.barkfluff.BarkCloud.data.gallery

import com.barkfluff.BarkCloud.data.cloud.CloudCopy
import com.barkfluff.BarkCloud.data.cloud.CloudRepository
import com.barkfluff.BarkCloud.data.cloud.HashPresence
import com.barkfluff.BarkCloud.data.upload.UploadQueueStore
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.mockk
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.IOException

class DeviceDeleteExecutorTest {

    private val hash = "f".repeat(64)
    private val key = "image:1"

    private class FakePending : PendingCleanupRepository {
        val entries = mutableListOf<PendingCloudCleanup>()
        override fun all(): List<PendingCloudCleanup> = entries.toList()
        override fun add(entry: PendingCloudCleanup) {
            entries.removeAll { it.mediaKey == entry.mediaKey }
            entries += entry
        }
        override fun remove(mediaKey: String) {
            entries.removeAll { it.mediaKey == mediaKey }
        }
    }

    private fun everywherePlan(mediaKey: String = key) = planDeviceDelete(
        DeviceDeleteMode.EVERYWHERE,
        listOf(DeviceDeleteItem(mediaKey, CloudCopy.Present("a", listOf("a", "b")))),
    )

    @Test
    fun deletesDeviceCopyAndEveryLiveCloudCopy() = runTest {
        val cloud = mockk<CloudRepository>(relaxed = true)
        val queue = mockk<UploadQueueStore>(relaxed = true)
        coEvery { cloud.checkFileHashesDetailed(listOf(hash)) } returns mapOf(hash to HashPresence(true, listOf("a", "b")))
        val pending = FakePending()
        val executor = DeviceDeleteExecutor(cloud, queue, FakeMediaCloudStateDao(), pending)

        val report = executor.finish(everywherePlan(), mapOf(key to hash))

        assertEquals(1, report.devicePhotos)
        assertEquals(2, report.cloudCopiesDeleted)
        assertEquals(0, report.failedPhotos)
        assertTrue(pending.entries.isEmpty())
        coVerify { queue.cancelLiveForMediaKey(key) }
        coVerify { cloud.deleteUserMedia("a") }
        coVerify { cloud.deleteUserMedia("b") }
    }

    @Test
    fun copyAttachedDuringConfirmationIsIncludedOnRecheck() = runTest {
        val cloud = mockk<CloudRepository>(relaxed = true)
        val queue = mockk<UploadQueueStore>(relaxed = true)
        coEvery { cloud.checkFileHashesDetailed(listOf(hash)) } returns mapOf(hash to HashPresence(true, listOf("a", "b", "c")))
        val executor = DeviceDeleteExecutor(cloud, queue, FakeMediaCloudStateDao(), FakePending())

        val report = executor.finish(everywherePlan(), mapOf(key to hash))

        assertEquals(3, report.cloudCopiesDeleted)
        coVerify { cloud.deleteUserMedia("c") }
    }

    @Test
    fun partialErrorKeepsDeviceDeletionAndRecordsOnePendingPhoto() = runTest {
        val cloud = mockk<CloudRepository>(relaxed = true)
        val queue = mockk<UploadQueueStore>(relaxed = true)
        coEvery { cloud.checkFileHashesDetailed(listOf(hash)) } returns mapOf(hash to HashPresence(true, listOf("a", "b")))
        coEvery { cloud.deleteUserMedia("b") } throws IOException("network")
        val pending = FakePending()
        val dao = FakeMediaCloudStateDao()
        val executor = DeviceDeleteExecutor(cloud, queue, dao, pending)

        val report = executor.finish(everywherePlan(), mapOf(key to hash))

        assertEquals(1, report.devicePhotos)
        assertEquals(1, report.cloudCopiesDeleted)
        assertEquals(1, report.failedPhotos)
        assertEquals(listOf(PendingCloudCleanup(key, hash)), pending.entries)
    }

    @Test
    fun unknownRecheckDoesNotDeleteAndStaysPending() = runTest {
        val cloud = mockk<CloudRepository>(relaxed = true)
        val queue = mockk<UploadQueueStore>(relaxed = true)
        coEvery { cloud.checkFileHashesDetailed(listOf(hash)) } returns emptyMap()
        val pending = FakePending()
        val executor = DeviceDeleteExecutor(cloud, queue, FakeMediaCloudStateDao(), pending)

        val report = executor.finish(everywherePlan(), mapOf(key to hash))

        assertEquals(0, report.cloudCopiesDeleted)
        assertEquals(1, report.failedPhotos)
        assertEquals(listOf(PendingCloudCleanup(key, hash)), pending.entries)
        coVerify(exactly = 0) { cloud.deleteUserMedia(any()) }
    }

    @Test
    fun retryClearsPendingPhotoOnceCopiesAreGone() = runTest {
        val cloud = mockk<CloudRepository>(relaxed = true)
        val queue = mockk<UploadQueueStore>(relaxed = true)
        coEvery { cloud.checkFileHashesDetailed(listOf(hash)) } returns mapOf(hash to HashPresence(false, emptyList()))
        val pending = FakePending().apply { add(PendingCloudCleanup(key, hash)) }
        val executor = DeviceDeleteExecutor(cloud, queue, FakeMediaCloudStateDao(), pending)

        val stillFailing = executor.retryPending()

        assertEquals(0, stillFailing)
        assertTrue(pending.entries.isEmpty())
    }

    @Test
    fun keepCloudDeletesOnlyDeviceCopyAndNeverTouchesCloud() = runTest {
        val cloud = mockk<CloudRepository>(relaxed = true)
        val queue = mockk<UploadQueueStore>(relaxed = true)
        val plan = planDeviceDelete(
            DeviceDeleteMode.KEEP_CLOUD,
            listOf(DeviceDeleteItem(key, CloudCopy.Present("a", listOf("a")))),
        )
        val executor = DeviceDeleteExecutor(cloud, queue, FakeMediaCloudStateDao(), FakePending())

        val report = executor.finish(plan, mapOf(key to hash))

        assertEquals(1, report.devicePhotos)
        assertEquals(0, report.cloudCopiesDeleted)
        coVerify(exactly = 0) { cloud.checkFileHashesDetailed(any()) }
        coVerify(exactly = 0) { cloud.deleteUserMedia(any()) }
        coVerify { queue.cancelLiveForMediaKey(key) }
    }

    @Test
    fun cancellationIsRethrownAndRecordsNothing() = runTest {
        val cloud = mockk<CloudRepository>(relaxed = true)
        val queue = mockk<UploadQueueStore>(relaxed = true)
        coEvery { cloud.checkFileHashesDetailed(listOf(hash)) } returns mapOf(hash to HashPresence(true, listOf("a")))
        coEvery { cloud.deleteUserMedia("a") } throws CancellationException("screen gone")
        val pending = FakePending()
        val executor = DeviceDeleteExecutor(cloud, queue, FakeMediaCloudStateDao(), pending)

        var thrown: CancellationException? = null
        try {
            executor.finish(everywherePlan(), mapOf(key to hash))
        } catch (error: CancellationException) {
            thrown = error
        }

        assertTrue(thrown != null)
        assertTrue(pending.entries.isEmpty())
    }
}
