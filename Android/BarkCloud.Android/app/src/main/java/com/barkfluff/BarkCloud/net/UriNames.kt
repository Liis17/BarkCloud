package com.barkfluff.BarkCloud.net

import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns

/** Человекочитаемое имя файла из content-[uri] (DISPLAY_NAME), с запасными вариантами. */
fun queryFileName(context: Context, uri: Uri): String {
    val name = runCatching {
        context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c ->
            if (c.moveToFirst()) c.getString(0) else null
        }
    }.getOrNull()
    return name?.takeIf { it.isNotBlank() }
        ?: uri.lastPathSegment?.substringAfterLast('/')
        ?: "file_${System.currentTimeMillis()}"
}
