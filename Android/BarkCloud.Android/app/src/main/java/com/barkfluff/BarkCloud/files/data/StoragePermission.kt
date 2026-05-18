package com.barkfluff.BarkCloud.files.data

import android.content.Intent
import android.net.Uri
import android.os.Environment
import android.provider.Settings

object StoragePermission {

    fun isGranted(): Boolean = Environment.isExternalStorageManager()

    fun requestIntent(packageName: String): Intent =
        Intent(Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION).apply {
            data = Uri.parse("package:$packageName")
        }

    fun fallbackIntent(): Intent = Intent(Settings.ACTION_MANAGE_ALL_FILES_ACCESS_PERMISSION)

    val externalRoot: java.io.File
        get() = Environment.getExternalStorageDirectory()
}
