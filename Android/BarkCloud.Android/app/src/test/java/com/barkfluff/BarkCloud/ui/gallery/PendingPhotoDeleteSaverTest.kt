package com.barkfluff.BarkCloud.ui.gallery

import androidx.compose.runtime.saveable.SaverScope
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class PendingPhotoDeleteSaverTest {
    private val scope = SaverScope { true }

    @Test
    fun pendingDeleteSurvivesSaveAndRestore() {
        val pending = PendingPhotoDelete(
            deviceMediaKeys = listOf("device:image:1", "device:video:2"),
            cloudFileIds = listOf("cloud-1"),
            totalCount = 3,
            accountReclaim = false,
        )

        val saved = with(pendingPhotoDeleteSaver) { scope.save(pending) }

        assertEquals(pending, pendingPhotoDeleteSaver.restore(requireNotNull(saved)))
    }

    @Test
    fun accountReclaimFlagSurvivesSaveAndRestore() {
        val pending = PendingPhotoDelete(
            deviceMediaKeys = listOf("device:image:1"),
            cloudFileIds = emptyList(),
            totalCount = 1,
            accountReclaim = true,
        )

        val saved = with(pendingPhotoDeleteSaver) { scope.save(pending) }

        assertEquals(pending, pendingPhotoDeleteSaver.restore(requireNotNull(saved)))
    }

    @Test
    fun absentPendingDeleteIsNotPersisted() {
        assertNull(with(pendingPhotoDeleteSaver) { scope.save(null) })
    }
}
