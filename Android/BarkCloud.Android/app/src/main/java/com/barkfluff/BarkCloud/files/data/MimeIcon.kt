package com.barkfluff.BarkCloud.files.data

import android.webkit.MimeTypeMap
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.InsertDriveFile
import androidx.compose.material.icons.outlined.Archive
import androidx.compose.material.icons.outlined.AudioFile
import androidx.compose.material.icons.outlined.Code
import androidx.compose.material.icons.outlined.Description
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.material.icons.outlined.Image
import androidx.compose.material.icons.outlined.Movie
import androidx.compose.material.icons.outlined.PictureAsPdf
import androidx.compose.ui.graphics.vector.ImageVector

object MimeIcon {

    fun mimeFor(fileName: String): String {
        val ext = fileName.substringAfterLast('.', missingDelimiterValue = "").lowercase()
        if (ext.isEmpty()) return "application/octet-stream"
        return MimeTypeMap.getSingleton().getMimeTypeFromExtension(ext) ?: "application/octet-stream"
    }

    fun iconFor(mime: String, fileName: String): ImageVector {
        if (mime.startsWith("image/")) return Icons.Outlined.Image
        if (mime.startsWith("video/")) return Icons.Outlined.Movie
        if (mime.startsWith("audio/")) return Icons.Outlined.AudioFile
        if (mime == "application/pdf") return Icons.Outlined.PictureAsPdf

        val ext = fileName.substringAfterLast('.', missingDelimiterValue = "").lowercase()
        return when (ext) {
            "zip", "rar", "7z", "tar", "gz", "bz2", "xz" -> Icons.Outlined.Archive
            "txt", "md", "rtf", "doc", "docx", "odt" -> Icons.Outlined.Description
            "json", "xml", "html", "htm", "css", "js", "ts", "kt", "java", "py", "c", "cpp", "h", "cs", "yml", "yaml" -> Icons.Outlined.Code
            else -> Icons.AutoMirrored.Outlined.InsertDriveFile
        }
    }

    val folderIcon: ImageVector = Icons.Outlined.Folder
}
