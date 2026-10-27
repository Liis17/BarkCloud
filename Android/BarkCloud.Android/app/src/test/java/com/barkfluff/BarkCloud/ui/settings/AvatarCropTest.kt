package com.barkfluff.BarkCloud.ui.settings

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class AvatarCropTest {
    @Test fun `default crop centers the largest square in landscape and portrait images`() {
        assertEquals(AvatarCropPixels(100, 0, 200), AvatarCrop().pixels(400, 200))
        assertEquals(AvatarCropPixels(0, 100, 200), AvatarCrop().pixels(200, 400))
    }

    @Test fun `dragging reveals the selected edge and zoom reduces the source region`() {
        assertEquals(AvatarCropPixels(200, 0, 200), AvatarCrop(panX = -0.5f).pixels(400, 200))
        assertEquals(AvatarCropPixels(0, 0, 200), AvatarCrop(panX = 0.5f).pixels(400, 200))
        assertEquals(AvatarCropPixels(150, 50, 100), AvatarCrop(zoom = 2f).pixels(400, 200))
    }

    @Test fun `extreme dragging never leaves blank space or samples outside the source`() {
        for (width in listOf(1, 3, 41, 80, 400, 2048)) {
            for (height in listOf(1, 7, 49, 120, 600, 2048)) {
                for (zoom in listOf(0.5f, 1f, 2f, 4f, 20f)) {
                    for (pan in listOf(-100f, -0.3f, 0f, 0.7f, 100f)) {
                        val pixels = AvatarCrop(zoom, pan, -pan).pixels(width, height)
                        assertTrue(pixels.side >= 1)
                        assertTrue(pixels.left >= 0 && pixels.top >= 0)
                        assertTrue(pixels.left + pixels.side <= width)
                        assertTrue(pixels.top + pixels.side <= height)
                    }
                }
            }
        }
    }
}
