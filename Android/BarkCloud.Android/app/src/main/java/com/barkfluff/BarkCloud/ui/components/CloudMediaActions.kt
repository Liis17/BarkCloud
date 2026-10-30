package com.barkfluff.BarkCloud.ui.components

import android.content.ClipData
import android.content.ClipboardManager
import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.MediaStore
import android.widget.Toast
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.ContentCopy
import androidx.compose.material.icons.outlined.Delete
import androidx.compose.material.icons.outlined.Download
import androidx.compose.material.icons.outlined.Info
import androidx.compose.material.icons.outlined.Share
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.HorizontalFloatingToolbar
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cache.LocalShareFileCache
import com.barkfluff.BarkCloud.data.cloud.MediaAsset
import com.barkfluff.BarkCloud.files.data.FileShareHelper
import com.barkfluff.BarkCloud.files.data.MimeIcon
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.io.IOException

private enum class CloudMediaAction {
    SHARE,
    COPY,
    DOWNLOAD,
    DELETE,
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
internal fun CloudMediaActions(
    asset: MediaAsset,
    infoEnabled: Boolean,
    onInfo: () -> Unit,
    onDelete: (suspend (MediaAsset) -> Unit)?,
    scope: CoroutineScope,
    modifier: Modifier = Modifier,
) {
    val context = LocalContext.current
    val app = context.applicationContext as BarkCloudApplication
    val actions = cloudMediaActionVisibility(
        isVideo = asset.isVideo,
        infoEnabled = infoEnabled,
        canDelete = onDelete != null,
    )
    var runningAction by remember(asset.id) { mutableStateOf<CloudMediaAction?>(null) }
    var deleteConfirmation by remember(asset.id) { mutableStateOf(false) }

    fun showToast(message: String) {
        Toast.makeText(context, message, Toast.LENGTH_SHORT).show()
    }

    fun runAction(
        action: CloudMediaAction,
        successMessage: Int? = null,
        block: suspend () -> Unit,
    ) {
        if (runningAction != null) return
        runningAction = action
        scope.launch {
            try {
                block()
                successMessage?.let { showToast(context.getString(it)) }
            } catch (error: CancellationException) {
                throw error
            } catch (_: Exception) {
                showToast(context.getString(R.string.viewer_action_failed))
            } finally {
                runningAction = null
            }
        }
    }

    suspend fun withExportCopy(action: suspend (File) -> Unit) {
        app.fileCache.withLoadedPinnedOriginal(asset.id, asset.fileName) { original ->
            val exportCopy = withContext(Dispatchers.IO) {
                LocalShareFileCache.prepare(context.cacheDir, original, displayName = asset.fileName)
            }
            action(exportCopy)
        }
    }

    HorizontalFloatingToolbar(
        expanded = true,
        modifier = modifier,
    ) {
        if (actions.share) {
            CloudMediaActionButton(
                icon = Icons.Outlined.Share,
                label = stringResource(R.string.viewer_action_share),
                loading = runningAction == CloudMediaAction.SHARE,
                enabled = runningAction == null,
                onClick = {
                    runAction(CloudMediaAction.SHARE) {
                        withExportCopy { exportCopy ->
                            withContext(Dispatchers.Main.immediate) {
                                val shareIntent = FileShareHelper.buildShareIntent(context, listOf(exportCopy))
                                context.startActivity(
                                    Intent.createChooser(
                                        shareIntent,
                                        context.getString(R.string.viewer_action_share_title),
                                    ),
                                )
                            }
                        }
                    }
                },
            )
        }
        if (actions.copy) {
            CloudMediaActionButton(
                icon = Icons.Outlined.ContentCopy,
                label = stringResource(R.string.viewer_action_copy),
                loading = runningAction == CloudMediaAction.COPY,
                enabled = runningAction == null,
                onClick = {
                    runAction(CloudMediaAction.COPY) {
                        withExportCopy { exportCopy ->
                            withContext(Dispatchers.Main.immediate) {
                                val uri = FileShareHelper.uriFor(context, exportCopy)
                                val clip = ClipData.newUri(context.contentResolver, asset.fileName, uri)
                                val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                                clipboard.setPrimaryClip(clip)
                                if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) {
                                    showToast(context.getString(R.string.viewer_action_copied))
                                }
                            }
                        }
                    }
                },
            )
        }
        if (actions.download) {
            CloudMediaActionButton(
                icon = Icons.Outlined.Download,
                label = stringResource(R.string.viewer_action_download),
                loading = runningAction == CloudMediaAction.DOWNLOAD,
                enabled = runningAction == null,
                onClick = {
                    runAction(CloudMediaAction.DOWNLOAD, R.string.viewer_action_download_complete) {
                        val target = cloudMediaStoreTarget(asset.isVideo, MimeIcon.mimeFor(asset.fileName))
                        app.fileCache.withLoadedPinnedOriginal(asset.id, asset.fileName) { original ->
                            saveToMediaStore(context, original, asset.fileName, target)
                        }
                    }
                },
            )
        }
        if (actions.info) {
            CloudMediaActionButton(
                icon = Icons.Outlined.Info,
                label = stringResource(R.string.viewer_action_info),
                enabled = runningAction == null,
                onClick = onInfo,
            )
        }
        if (actions.delete) {
            CloudMediaActionButton(
                icon = Icons.Outlined.Delete,
                label = stringResource(R.string.viewer_action_delete),
                loading = runningAction == CloudMediaAction.DELETE,
                enabled = runningAction == null,
                onClick = { deleteConfirmation = true },
            )
        }
    }

    if (deleteConfirmation) {
        AlertDialog(
            onDismissRequest = { if (runningAction != CloudMediaAction.DELETE) deleteConfirmation = false },
            title = { Text(stringResource(R.string.viewer_action_delete_title)) },
            text = { Text(stringResource(R.string.viewer_action_delete_message, asset.fileName)) },
            confirmButton = {
                TextButton(
                    enabled = runningAction == null,
                    onClick = {
                        val delete = onDelete
                        if (delete != null && runningAction == null) {
                            runningAction = CloudMediaAction.DELETE
                            scope.launch {
                                try {
                                    delete(asset)
                                    deleteConfirmation = false
                                } catch (error: CancellationException) {
                                    throw error
                                } catch (_: Exception) {
                                    showToast(context.getString(R.string.viewer_action_failed))
                                } finally {
                                    runningAction = null
                                }
                            }
                        }
                    },
                ) {
                    if (runningAction == CloudMediaAction.DELETE) {
                        CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
                    } else {
                        Text(stringResource(R.string.viewer_action_delete))
                    }
                }
            },
            dismissButton = {
                TextButton(
                    enabled = runningAction == null,
                    onClick = { deleteConfirmation = false },
                ) {
                    Text(stringResource(R.string.viewer_action_cancel))
                }
            },
        )
    }
}

@Composable
private fun CloudMediaActionButton(
    icon: ImageVector,
    label: String,
    enabled: Boolean,
    loading: Boolean = false,
    onClick: () -> Unit,
) {
    IconButton(onClick = onClick, enabled = enabled) {
        if (loading) {
            CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
        } else {
            Icon(imageVector = icon, contentDescription = label)
        }
    }
}

private suspend fun saveToMediaStore(
    context: Context,
    source: File,
    fileName: String,
    target: CloudMediaStoreTarget,
) = withContext(Dispatchers.IO) {
    val collection = when (target.collection) {
        CloudMediaStoreCollection.IMAGES -> MediaStore.Images.Media.EXTERNAL_CONTENT_URI
        CloudMediaStoreCollection.VIDEOS -> MediaStore.Video.Media.EXTERNAL_CONTENT_URI
    }
    val values = ContentValues().apply {
        put(MediaStore.MediaColumns.DISPLAY_NAME, fileName)
        put(MediaStore.MediaColumns.MIME_TYPE, target.mimeType)
        put(MediaStore.MediaColumns.RELATIVE_PATH, target.relativePath)
        put(MediaStore.MediaColumns.IS_PENDING, 1)
    }
    val resolver = context.contentResolver
    var uri: Uri? = null
    var published = false
    try {
        val insertedUri = resolver.insert(collection, values)
            ?: throw IOException("MediaStore could not create the file")
        uri = insertedUri
        resolver.openOutputStream(insertedUri, "w")?.use { output ->
            source.inputStream().use { input -> input.copyTo(output) }
        } ?: throw IOException("MediaStore could not open the file")
        val result = resolver.update(
            insertedUri,
            ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) },
            null,
            null,
        )
        if (result <= 0) throw IOException("MediaStore could not publish the file")
        published = true
    } finally {
        if (!published) uri?.let { pendingUri -> runCatching { resolver.delete(pendingUri, null, null) } }
    }
}
