package com.barkfluff.BarkCloud.ui.media

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.grid.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.PlayCircle
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import coil3.compose.AsyncImage
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.gallery.DeviceMedia
import com.barkfluff.BarkCloud.data.gallery.DeviceMediaStore
import com.barkfluff.BarkCloud.files.ui.formatChildCount
import com.barkfluff.BarkCloud.ui.components.RefreshableContentSurface
import com.barkfluff.BarkCloud.ui.gallery.DeviceMediaViewer
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

internal data class DeviceFolder(val key: String, val name: String, val media: List<DeviceMedia>)

internal fun deviceFolders(media: List<DeviceMedia>): List<DeviceFolder> =
    media.groupBy { it.folderKey }.map { (key, items) ->
        DeviceFolder(key, items.first().folderName, items.sortedByDescending { it.dateTakenMillis })
    }.sortedBy { it.name.lowercase() }

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DeviceFoldersScreen(onNavigateUp: () -> Unit) {
    val context = LocalContext.current
    var folders by remember { mutableStateOf(emptyList<DeviceFolder>()) }
    var selectedKey by rememberSaveable { mutableStateOf<String?>(null) }
    var viewer by remember { mutableStateOf<DeviceMedia?>(null) }
    var revision by remember { mutableIntStateOf(0) }
    var loading by remember { mutableStateOf(true) }
    var hasAccess by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf(false) }
    val permissions = remember {
        if (Build.VERSION.SDK_INT >= 33) {
            arrayOf(Manifest.permission.READ_MEDIA_IMAGES, Manifest.permission.READ_MEDIA_VIDEO) +
                if (Build.VERSION.SDK_INT >= 34) arrayOf(Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED) else emptyArray()
        } else arrayOf(Manifest.permission.READ_EXTERNAL_STORAGE)
    }
    val permissionLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { revision++ }
    LifecycleEventEffect(Lifecycle.Event.ON_RESUME) { revision++ }

    suspend fun reload() {
        fun granted(permission: String) = ContextCompat.checkSelfPermission(context, permission) == PackageManager.PERMISSION_GRANTED
        val partial = Build.VERSION.SDK_INT >= 34 && granted(Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED)
        val images = granted(if (Build.VERSION.SDK_INT >= 33) Manifest.permission.READ_MEDIA_IMAGES else Manifest.permission.READ_EXTERNAL_STORAGE) || partial
        val videos = granted(if (Build.VERSION.SDK_INT >= 33) Manifest.permission.READ_MEDIA_VIDEO else Manifest.permission.READ_EXTERNAL_STORAGE) || partial
        hasAccess = images || videos
        loading = true
        error = false
        try {
            folders = if (hasAccess) withContext(Dispatchers.IO) {
                deviceFolders(DeviceMediaStore.query(context, images, videos))
            } else emptyList()
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            error = true
        } finally {
            loading = false
        }
    }
    LaunchedEffect(revision) { reload() }
    val selected = folders.firstOrNull { it.key == selectedKey }
    val back = { if (selectedKey != null) selectedKey = null else onNavigateUp() }
    BackHandler(enabled = selectedKey != null) { selectedKey = null }

    RefreshableContentSurface(
        onRefresh = { reload() },
        modifier = Modifier.fillMaxSize(),
        header = {
            CenterAlignedTopAppBar(
                title = { Text(selected?.name ?: stringResource(R.string.collections_device)) },
                windowInsets = WindowInsets(0, 0, 0, 0),
                navigationIcon = {
                    IconButton(onClick = back) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, stringResource(R.string.files_back))
                    }
                },
            )
        },
    ) {
        if (loading && folders.isEmpty()) {
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
        } else if (!hasAccess || error || folders.isEmpty()) {
            Column(Modifier.fillMaxSize().padding(24.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) {
                Text(stringResource(if (error) R.string.device_media_error else if (!hasAccess) R.string.device_media_permission else R.string.device_folders_empty))
                if (!hasAccess) Button(onClick = { permissionLauncher.launch(permissions) }) { Text(stringResource(R.string.device_media_allow)) }
            }
        } else if (selectedKey != null) {
            LazyVerticalGrid(
                columns = GridCells.Adaptive(110.dp),
                modifier = Modifier.fillMaxSize(),
                contentPadding = PaddingValues(12.dp),
                horizontalArrangement = Arrangement.spacedBy(4.dp),
                verticalArrangement = Arrangement.spacedBy(4.dp),
            ) {
                items(selected?.media.orEmpty(), key = { it.mediaKey }) { media ->
                    Box(Modifier.aspectRatio(1f).clip(MaterialTheme.shapes.small).clickable { viewer = media }) {
                        AsyncImage(media.uri, media.name, Modifier.fillMaxSize(), contentScale = ContentScale.Crop)
                        if (media.isVideo) Icon(Icons.Filled.PlayCircle, stringResource(R.string.viewer_play_video), Modifier.align(Alignment.Center))
                    }
                }
            }
        } else {
            LazyVerticalGrid(
                columns = GridCells.Adaptive(180.dp),
                modifier = Modifier.fillMaxSize(),
                contentPadding = PaddingValues(12.dp),
                horizontalArrangement = Arrangement.spacedBy(12.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                items(folders, key = { it.key }) { folder ->
                    ElevatedCard(Modifier.fillMaxWidth().clickable { selectedKey = folder.key }) {
                        AsyncImage(folder.media.first().uri, folder.name, Modifier.fillMaxWidth().aspectRatio(1f).clip(RoundedCornerShape(topStart = 12.dp, topEnd = 12.dp)), contentScale = ContentScale.Crop)
                        Column(Modifier.padding(12.dp)) {
                            Text(folder.name, style = MaterialTheme.typography.titleMedium, maxLines = 1)
                            Text(formatChildCount(context, folder.media.size), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                }
            }
        }
    }
    viewer?.let { media ->
        DeviceMediaViewer(media, onDismiss = { viewer = null }, onPlayVideo = {
            val intent = Intent(Intent.ACTION_VIEW).setDataAndType(media.uri, "video/*").addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            runCatching { context.startActivity(intent) }
        })
    }
}
