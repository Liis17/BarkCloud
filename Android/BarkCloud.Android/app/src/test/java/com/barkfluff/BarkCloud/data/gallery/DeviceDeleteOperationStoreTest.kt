package com.barkfluff.BarkCloud.data.gallery

import com.barkfluff.BarkCloud.data.cache.CacheScope
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

class DeviceDeleteOperationStoreTest {

    @get:Rule
    val folder = TemporaryFolder()

    private fun sample(id: String = "op-1") = DeleteOperation(
        id = id,
        scope = CacheScope("srv", "acct"),
        mode = DeviceDeleteMode.EVERYWHERE,
        phase = DeleteOpPhase.APPROVED,
        devicePhotos = listOf("image:1"),
        photos = listOf(
            OpPhoto(
                mediaKey = "image:1",
                hash = "a".repeat(64),
                confirmedIds = listOf("x", "y"),
                hiddenIds = listOf("y"),
                doneIds = listOf("x"),
            ),
        ),
    )

    @Test
    fun roundTripSurvivesNewStoreInstance() {
        val file = File(folder.root, "ops.json")
        DeviceDeleteOperationStore(file).put(sample())

        val restored = DeviceDeleteOperationStore(file).get("op-1")

        assertEquals(sample(), restored)
        assertEquals(listOf("y"), restored!!.photos.single().remainingIds)
    }

    @Test
    fun removeDropsOnlyThatOperation() {
        val file = File(folder.root, "ops.json")
        val store = DeviceDeleteOperationStore(file)
        store.put(sample("op-1"))
        store.put(sample("op-2"))

        store.remove("op-1")

        assertEquals(listOf("op-2"), store.all().map { it.id })
    }

    @Test
    fun corruptFileIsMovedAsideNotSilentlyDeleted() {
        val file = File(folder.root, "ops.json")
        file.writeText("{not json")

        val store = DeviceDeleteOperationStore(file)

        assertEquals(emptyList<DeleteOperation>(), store.all())
        assertTrue(File(folder.root, "ops.json.corrupt").exists())
    }
}
