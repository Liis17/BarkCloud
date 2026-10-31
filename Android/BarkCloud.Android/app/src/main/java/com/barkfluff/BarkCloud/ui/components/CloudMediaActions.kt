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
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.outlined.MoreVert
import androidx.compose.material.icons.outlined.FavoriteBorder
import androidx.compose.material.icons.outlined.PhotoAlbum
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import com.barkfluff.BarkCloud.data.cloud.AlbumCard
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
    FAVORITE,
    ALBUM,
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
    overflowOnly: Boolean = false,
) {
    val context = LocalContext.current
    val app = context.applicationContext as BarkCloudApplication
    val actions = cloudMediaActionVisibility(
        isVideo = asset.isVideo,
        infoEnabled = infoEnabled,
        canDelete = onDelete != null,
    )
    var runningAction by remember(asset.id) { mutableStateOf<CloudMediaAction?>(null) }
    var menuOpen by remember(asset.id) { mutableStateOf(false) }
    var albums by remember(asset.id) { mutableStateOf<List<AlbumCard>?>(null) }
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
        menuOpen = false
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

    val actionContent: @Composable () -> Unit = {
        if (!overflowOnly) {
            CloudMediaActionButton(Icons.Outlined.PhotoAlbum, stringResource(R.string.viewer_action_album), runningAction == null, onClick = {
                runAction(CloudMediaAction.ALBUM) {
                    val result = mutableListOf<AlbumCard>()
                    var page = app.albumRepository.listAlbums()
                    result.addAll(page.albums)
                    while (page.hasMore) {
                        page = app.albumRepository.listAlbums(cursorUpdatedAtMillis = page.nextCursorUpdatedAtMillis, cursorAlbumId = page.nextCursorAlbumId)
                        result.addAll(page.albums)
                    }
                    albums = result
                }
            })
            CloudMediaActionButton(Icons.Outlined.FavoriteBorder, stringResource(R.string.media_add_favorite), runningAction == null, onClick = {
                runAction(CloudMediaAction.FAVORITE, R.string.viewer_favorite_added) { app.cloudRepository.addFavorite(asset.id) }
            })
        }
        if (!overflowOnly && actions.share) {
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
        if (overflowOnly && actions.copy) {
            CloudMediaActionButton(
                icon = Icons.Outlined.ContentCopy,
                menuItem = true,
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
        if (overflowOnly && actions.download) {
            CloudMediaActionButton(
                icon = Icons.Outlined.Download,
                menuItem = true,
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
        if (overflowOnly && actions.info) {
            CloudMediaActionButton(
                icon = Icons.Outlined.Info,
                menuItem = true,
                label = stringResource(R.string.viewer_action_info),
                enabled = runningAction == null,
                onClick = { menuOpen = false; onInfo() },
            )
        }
        if (!overflowOnly && actions.delete) {
            CloudMediaActionButton(
                icon = Icons.Outlined.Delete,
                label = stringResource(R.string.viewer_action_delete),
                loading = runningAction == CloudMediaAction.DELETE,
                enabled = runningAction == null,
                onClick = { deleteConfirmation = true },
            )
        }
    }

    if (overflowOnly) {
        Box(modifier) {
            IconButton(onClick = { menuOpen = true }) {
                Icon(Icons.Outlined.MoreVert, stringResource(R.string.files_action_more), tint = androidx.compose.ui.graphics.Color.White)
            }
            DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) { actionContent() }
        }
    } else {
        HorizontalFloatingToolbar(expanded = true, modifier = modifier) { actionContent() }
    }
    albums?.let { choices ->
        AlertDialog(
            onDismissRequest = { albums = null },
            title = { Text(stringResource(R.string.viewer_action_album)) },
            text = {
                Column(Modifier.verticalScroll(rememberScrollState())) {
                    if (choices.isEmpty()) Text(stringResource(R.string.viewer_albums_empty))
                    choices.forEach { album ->
                        TextButton(enabled = runningAction == null, onClick = {
                            runAction(CloudMediaAction.ALBUM, R.string.viewer_album_added) {
                                app.albumRepository.addItems(album.id, listOf(asset.id))
                                albums = null
                            }
                        }) { Text(album.name) }
                    }
                }
            },
            confirmButton = { TextButton(onClick = { albums = null }) { Text(stringResource(R.string.viewer_action_cancel)) } },
        )
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
    menuItem: Boolean = false,
    onClick: () -> Unit,
) {
    if (menuItem) {
        DropdownMenuItem(text = { Text(label) }, leadingIcon = { Icon(icon, null) }, enabled = enabled, onClick = onClick)
        return
    }
    IconButton(modifier = Modifier.width(64.dp).size(56.dp), onClick = onClick, enabled = enabled) {
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
