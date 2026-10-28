package com.barkfluff.BarkCloud.ui.media

import android.net.Uri
import com.barkfluff.BarkCloud.data.gallery.DeviceMedia
import io.mockk.mockk
import org.junit.Assert.*
import org.junit.Test

class DeviceFoldersTest {
    private fun media(id: Long, folder: String, name: String, date: Long, video: Boolean = false) =
        DeviceMedia(id, mockk<Uri>(), video, "file", date, 0, 1, folder, name)

    @Test fun `photos and videos share a folder and newest media is the cover`() {
        val photo = media(1, "primary:1", "Camera", 10)
        val video = media(2, "primary:1", "Camera", 20, video = true)
        val folders = deviceFolders(listOf(photo, video))
        assertEquals(1, folders.size)
        assertEquals(listOf(video, photo), folders.single().media)
    }

    @Test fun `same names in different folders or volumes remain separate`() {
        val folders = deviceFolders(listOf(
            media(1, "primary:1", "Camera", 10),
            media(2, "sdcard:1", "Camera", 10),
            media(3, "primary:2", "Camera", 10),
        ))
        assertEquals(3, folders.size)
        assertTrue(folders.all { it.media.size == 1 })
    }

    @Test fun `empty media library has no folders`() {
        assertTrue(deviceFolders(emptyList()).isEmpty())
    }
}
