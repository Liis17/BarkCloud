package com.barkfluff.BarkCloud.ui.shared

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.Download
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cloud.PublicDir
import com.barkfluff.BarkCloud.data.cloud.PublicFile
import com.barkfluff.BarkCloud.files.data.FileShareHelper
import com.barkfluff.BarkCloud.files.data.MimeIcon
import com.barkfluff.BarkCloud.ui.components.MediaThumb

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SharedFolderBrowserScreen(
    directoryId: String,
    title: String,
    onOpenFolder: (id: String, name: String) -> Unit,
    onNavigateUp: () -> Unit,
    viewModel: SharedFolderBrowserViewModel = viewModel(factory = SharedFolderBrowserViewModel.factory()),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val snackbarHostState = remember { SnackbarHostState() }
    val context = LocalContext.current

    LaunchedEffect(directoryId) { viewModel.start(directoryId) }
    LaunchedEffect(state.snackbar) {
        val msg = state.snackbar ?: return@LaunchedEffect
        snackbarHostState.showSnackbar(msg)
        viewModel.snackbarShown()
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(state.name.ifEmpty { title }, maxLines = 1) },
                navigationIcon = {
                    IconButton(onClick = onNavigateUp) {
                        Icon(Icons.AutoMirrored.Outlined.ArrowBack, contentDescription = null)
                    }
                },
            )
        },
        snackbarHost = { SnackbarHost(snackbarHostState) },
    ) { padding ->
        Box(Modifier.fillMaxSize().padding(padding)) {
            LazyColumn(Modifier.fillMaxSize()) {
                items(state.subdirs, key = { "d-${it.id}" }) { dir ->
                    SharedDirRow(dir, onOpen = { onOpenFolder(dir.id, dir.name) })
                }
                items(state.files, key = { "f-${it.id}" }) { file ->
                    SharedFileRow(
                        file = file,
                        isDownloading = state.downloadingFileId == file.id,
                        onDownload = {
                            viewModel.download(file) { downloaded ->
                                val mime = MimeIcon.mimeFor(file.name)
                                context.startActivity(FileShareHelper.buildOpenIntent(context, downloaded, mime))
                            }
                        },
                    )
                }
            }

            if (state.isLoading) {
                CircularProgressIndicator(Modifier.align(Alignment.Center))
            } else if (!state.found) {
                Text(
                    text = stringResource(R.string.files_empty_folder),
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.align(Alignment.Center),
                )
            }
        }
    }
}

@Composable
private fun SharedDirRow(dir: PublicDir, onOpen: () -> Unit) {
    ListItem(
        headlineContent = { Text(dir.name, maxLines = 1) },
        leadingContent = { Icon(Icons.Outlined.Folder, contentDescription = null) },
        modifier = Modifier.clickable(onClick = onOpen),
    )
}

@Composable
private fun SharedFileRow(
    file: PublicFile,
    isDownloading: Boolean,
    onDownload: () -> Unit,
) {
    ListItem(
        headlineContent = { Text(file.name, maxLines = 1) },
        leadingContent = {
            if (file.previewUrl != null) {
                MediaThumb(
                    model = file.previewUrl,
                    isVideo = file.kind.isVideo,
                    contentDescription = file.name,
                    modifier = Modifier.size(40.dp).clip(RoundedCornerShape(8.dp)),
                )
            } else {
                Icon(MimeIcon.iconFor(MimeIcon.mimeFor(file.name), file.name), contentDescription = null)
            }
        },
        trailingContent = {
            if (isDownloading) {
                CircularProgressIndicator(Modifier.size(24.dp))
            } else {
                IconButton(onClick = onDownload) {
                    Icon(Icons.Outlined.Download, contentDescription = stringResource(R.string.shared_download))
                }
            }
        },
    )
}
