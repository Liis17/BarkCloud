package com.barkfluff.BarkCloud

import android.content.Context
import android.content.ContextWrapper
import androidx.test.core.app.ApplicationProvider
import com.barkfluff.BarkCloud.data.gallery.AutoUploadNetworkPolicy
import com.barkfluff.BarkCloud.data.gallery.AutoUploadSettings
import org.junit.Assert.*
import org.junit.After
import org.junit.Test

class FolderBackupSettingsTest {
    private val base = ApplicationProvider.getApplicationContext<Context>()
    private val name = "folder-backup-test-${java.util.UUID.randomUUID()}"
    private val context = object : ContextWrapper(base) {
        override fun getSharedPreferences(ignored: String, mode: Int) = base.getSharedPreferences(name, mode)
    }
    @After fun clear() { base.deleteSharedPreferences(name) }

    @Test fun enablingFromOffSelectsOnlyThatFolderAndPersists() {
        val settings = AutoUploadSettings(context)
        settings.setFolderEnabled("volume:1", true, setOf("volume:1", "volume:2"))
        val restored = AutoUploadSettings(context)
        assertEquals(AutoUploadNetworkPolicy.WIFI_ONLY, restored.policy)
        assertTrue(restored.includesFolder("volume:1"))
        assertFalse(restored.includesFolder("volume:2"))
        assertFalse(restored.includesFolder("other-volume:1"))
    }

    @Test fun disablingAndReenablingPreservesOtherFoldersAndNetworkPolicy() {
        val settings = AutoUploadSettings(context)
        settings.policy = AutoUploadNetworkPolicy.ANY_NETWORK
        settings.setFolderEnabled("a", false, setOf("a", "b"))
        assertFalse(settings.includesFolder("a"))
        assertTrue(settings.includesFolder("b"))
        settings.setFolderEnabled("a", true, setOf("a", "b"))
        assertTrue(settings.includesFolder("a"))
        assertEquals(AutoUploadNetworkPolicy.ANY_NETWORK, settings.policy)
    }

    @Test fun existingGlobalBackupStillIncludesAllFoldersUntilSelection() {
        val settings = AutoUploadSettings(context)
        settings.policy = AutoUploadNetworkPolicy.WIFI_ONLY
        assertTrue(settings.includesFolder("existing"))
        assertTrue(settings.includesFolder("new"))
    }
}
