package com.barkfluff.BarkCloud.ui.settings

import kotlin.math.min
import kotlin.math.roundToInt

/** Pan is measured in fractions of the square viewport, independent of screen density. */
internal data class AvatarCrop(val zoom: Float = 1f, val panX: Float = 0f, val panY: Float = 0f) {
    fun constrained(width: Int, height: Int): AvatarCrop {
        val boundedZoom = zoom.coerceIn(1f, 4f)
        val scale = boundedZoom / min(width, height)
        val maxX = ((width * scale - 1f) / 2f).coerceAtLeast(0f)
        val maxY = ((height * scale - 1f) / 2f).coerceAtLeast(0f)
        return AvatarCrop(boundedZoom, panX.coerceIn(-maxX, maxX), panY.coerceIn(-maxY, maxY))
    }

    fun pixels(width: Int, height: Int): AvatarCropPixels {
        val crop = constrained(width, height)
        val scale = crop.zoom / min(width, height)
        val side = (1f / scale).roundToInt().coerceIn(1, min(width, height))
        val left = (((width * scale - 1f) / 2f - crop.panX) / scale).roundToInt().coerceIn(0, width - side)
        val top = (((height * scale - 1f) / 2f - crop.panY) / scale).roundToInt().coerceIn(0, height - side)
        return AvatarCropPixels(left, top, side)
    }
}

internal data class AvatarCropPixels(val left: Int, val top: Int, val side: Int)
