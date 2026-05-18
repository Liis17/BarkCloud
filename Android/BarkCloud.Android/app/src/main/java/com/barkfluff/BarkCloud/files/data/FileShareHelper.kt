package com.barkfluff.BarkCloud.files.data

import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.core.content.FileProvider
import com.barkfluff.BarkCloud.BuildConfig
import java.io.File

object FileShareHelper {

    private fun authority(): String = "${BuildConfig.APPLICATION_ID}.fileprovider"

    fun uriFor(context: Context, file: File): Uri =
        FileProvider.getUriForFile(context, authority(), file)

    fun buildOpenIntent(context: Context, file: File, mime: String): Intent {
        val uri = uriFor(context, file)
        return Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, mime)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }
    }

    fun buildShareIntent(context: Context, files: List<File>): Intent {
        if (files.size == 1) {
            val file = files.first()
            val mime = MimeIcon.mimeFor(file.name)
            val uri = uriFor(context, file)
            return Intent(Intent.ACTION_SEND).apply {
                type = mime
                putExtra(Intent.EXTRA_STREAM, uri)
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            }
        }
        val uris = ArrayList(files.map { uriFor(context, it) })
        return Intent(Intent.ACTION_SEND_MULTIPLE).apply {
            type = "*/*"
            putParcelableArrayListExtra(Intent.EXTRA_STREAM, uris)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }
    }
}
