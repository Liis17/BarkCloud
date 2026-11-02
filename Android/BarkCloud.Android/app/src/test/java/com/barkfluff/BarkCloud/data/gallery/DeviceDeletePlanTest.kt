package com.barkfluff.BarkCloud.data.gallery

import com.barkfluff.BarkCloud.data.cloud.CloudCopy
import com.barkfluff.BarkCloud.data.cloud.HashPresence
import org.junit.Assert.assertEquals
import org.junit.Test

class DeviceDeletePlanTest {

    private val present = CloudCopy.Present("a", listOf("a", "b"))
    private val items = listOf(
        DeviceDeleteItem("image:1", present),
        DeviceDeleteItem("image:2", CloudCopy.Absent),
        DeviceDeleteItem("image:3", CloudCopy.Unknown),
    )

    @Test
    fun keepCloudDeletesOnlyConfirmedCloudCopies() {
        val plan = planDeviceDelete(DeviceDeleteMode.KEEP_CLOUD, items)

        assertEquals(listOf("image:1"), plan.devicePhotos)
        assertEquals(emptyMap<String, List<String>>(), plan.cloudCopies)
        assertEquals(listOf("image:2", "image:3"), plan.skipped)
        assertEquals(0, plan.cloudCopyCount)
    }

    @Test
    fun everywhereDeletesAllLiveCopiesAndLocalOnlyPhotos() {
        val plan = planDeviceDelete(DeviceDeleteMode.EVERYWHERE, items)

        assertEquals(listOf("image:1", "image:2"), plan.devicePhotos)
        assertEquals(mapOf("image:1" to listOf("a", "b")), plan.cloudCopies)
        assertEquals(listOf("image:3"), plan.skipped)
        assertEquals(2, plan.cloudCopyCount)
    }

    @Test
    fun unknownStateNeverDeletesAnything() {
        val plan = planDeviceDelete(DeviceDeleteMode.EVERYWHERE, listOf(DeviceDeleteItem("image:9", CloudCopy.Unknown)))

        assertEquals(emptyList<String>(), plan.devicePhotos)
        assertEquals(listOf("image:9"), plan.skipped)
    }

    @Test
    fun cleanupSkipsPresenceWithoutIdsAndReportsItAsUnconfirmed() {
        // Сервер без file_ids: exists=true, но ID нет — копия не подтверждена, удалять нельзя.
        val withoutIds = CloudCopy.of(HashPresence(exists = true, fileIds = emptyList()))
        val plan = planDeviceCopyCleanup(
            listOf(
                DeviceDeleteItem("image:1", present),
                DeviceDeleteItem("image:2", CloudCopy.Absent),
                DeviceDeleteItem("image:3", withoutIds),
            ),
        )

        assertEquals(listOf("image:1"), plan.devicePhotos)
        assertEquals(1, plan.unconfirmed)
    }

    @Test
    fun hiddenCopiesAreReportedForUnlockGate() {
        val plan = planDeviceDelete(DeviceDeleteMode.EVERYWHERE, items)

        assertEquals(setOf("b"), plan.hiddenCopyIds(setOf("b", "zzz")))
        assertEquals(emptySet<String>(), plan.hiddenCopyIds(emptySet()))
    }
}
