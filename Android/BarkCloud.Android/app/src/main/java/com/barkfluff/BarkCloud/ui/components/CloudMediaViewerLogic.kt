package com.barkfluff.BarkCloud.ui.components

import java.util.Locale

private val jpegViewRequiredExtensions = setOf(
    "heic", "heif", "svg", "tif", "tiff", "avif", "jxl",
    "dng", "cr2", "cr3", "nef", "nrw", "arw", "orf", "rw2",
)

internal fun requiresJpegView(fileName: String, hasJpegView: Boolean): Boolean {
    if (!hasJpegView) return false
    val extension = fileName.substringAfterLast('.', "").lowercase(Locale.ROOT)
    return extension in jpegViewRequiredExtensions
}
