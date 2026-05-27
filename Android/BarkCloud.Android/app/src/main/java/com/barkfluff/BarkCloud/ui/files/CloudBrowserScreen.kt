package com.barkfluff.BarkCloud.ui.files

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.outlined.CreateNewFolder
import androidx.compose.material.icons.outlined.Delete
import androidx.compose.material.icons.outlined.DriveFileMove
import androidx.compose.material.icons.outlined.Edit
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.material.icons.outlined.InsertDriveFile
import androidx.compose.material.icons.outlined.MoreVert
import androidx.compose.material.icons.outlined.UploadFile
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cloud.CloudFileEntry
import com.barkfluff.BarkCloud.files.data.MimeIcon
import com.barkfluff.BarkCloud.ui.components.MediaThumb
import com.barkfluff.BarkCloud.ui.components.TextInputDialog
import com.barkfluff.BarkCloud.ui.components.rememberRemoteOpener

private sealed interface CloudTarget {
    data class Dir(val id: String, val name: String) : CloudTarget
    data class File(val entryId: String, val name: String) : CloudTarget
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun CloudBrowserScreen(
    directoryId: String,
    title: String,
    onOpenFolder: (id: String, name: String) -> Unit,
    onNavigateUp: () -> Unit,
    viewModel: CloudBrowserViewModel = viewModel(factory = CloudBrowserViewModel.factory()),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val snackbarHostState = remember { SnackbarHostState() }
    val openRemote = rememberRemoteOpener()

    var fabMenu by remember { mutableStateOf(false) }
    var showCreate by remember { mutableStateOf(false) }
    var renameTarget by remember { mutableStateOf<CloudTarget?>(null) }
    var deleteTarget by remember { mutableStateOf<CloudTarget?>(null) }
    var moveTarget by remember { mutableStateOf<CloudTarget?>(null) }

    val mediaPicker = rememberLauncherForActivityResult(
        ActivityResultContracts.PickMultipleVisualMedia(),
    ) { uris -> if (uris.isNotEmpty()) viewModel.upload(uris) }
    val docPicker = rememberLauncherForActivityResult(
        ActivityResultContracts.OpenMultipleDocuments(),
    ) { uris -> if (uris.isNotEmpty()) viewModel.upload(uris) }

    LaunchedEffect(directoryId) { viewModel.start(directoryId) }
    LaunchedEffect(state.snackbar) {
        val msg = state.snackbar ?: return@LaunchedEffect
        snackbarHostState.showSnackbar(msg)
        viewModel.snackbarShown()
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(title, maxLines = 1) },
                navigationIcon = {
                    IconButton(onClick = onNavigateUp) {
                        Icon(Icons.AutoMirrored.Outlined.ArrowBack, contentDescription = null)
                    }
                },
            )
        },
        snackbarHost = { SnackbarHost(snackbarHostState) },
        floatingActionButton = {
            Box {
                FloatingActionButton(onClick = { fabMenu = true }) {
                    Icon(Icons.Filled.Add, contentDescription = null)
                }
                DropdownMenu(expanded = fabMenu, onDismissRequest = { fabMenu = false }) {
                    DropdownMenuItem(
                        text = { Text(stringResource(R.string.files_action_create_folder)) },
                        leadingIcon = { Icon(Icons.Outlined.CreateNewFolder, contentDescription = null) },
                        onClick = { fabMenu = false; showCreate = true },
                    )
                    DropdownMenuItem(
                        text = { Text(stringResource(R.string.cloud_upload_media)) },
                        leadingIcon = { Icon(Icons.Outlined.UploadFile, contentDescription = null) },
                        onClick = {
                            fabMenu = false
                            mediaPicker.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageAndVideo))
                        },
                    )
                    DropdownMenuItem(
                        text = { Text(stringResource(R.string.cloud_upload_document)) },
                        leadingIcon = { Icon(Icons.Outlined.InsertDriveFile, contentDescription = null) },
                        onClick = { fabMenu = false; docPicker.launch(arrayOf("*/*")) },
                    )
                }
            }
        },
    ) { padding ->
        Box(Modifier.fillMaxSize().padding(padding)) {
            LazyColumn(Modifier.fillMaxSize()) {
                if (state.crumbs.isNotEmpty()) {
                    item { Breadcrumbs(state.crumbs.map { it.id to it.name }, onOpenFolder) }
                }
                items(state.subdirs, key = { "d-${it.id}" }) { dir ->
                    ListItem(
                        headlineContent = { Text(dir.name) },
                        leadingContent = { Icon(Icons.Outlined.Folder, contentDescription = null) },
                        trailingContent = {
                            ItemMenu(
                                onRename = { renameTarget = CloudTarget.Dir(dir.id, dir.name) },
                                onMove = { moveTarget = CloudTarget.Dir(dir.id, dir.name) },
                                onDelete = { deleteTarget = CloudTarget.Dir(dir.id, dir.name) },
                            )
                        },
                        modifier = Modifier.clickable { onOpenFolder(dir.id, dir.name) },
                    )
                }
                items(state.files, key = { "f-${it.id}" }) { file ->
                    FileRow(
                        file = file,
                        onOpen = { openRemote(file.fileId, file.name) { } },
                        onRename = { renameTarget = CloudTarget.File(file.id, file.name) },
                        onMove = { moveTarget = CloudTarget.File(file.id, file.name) },
                        onDelete = { deleteTarget = CloudTarget.File(file.id, file.name) },
                    )
                }
            }

            if (state.isEmpty) {
                Text(
                    text = stringResource(R.string.files_empty_folder),
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.align(Alignment.Center),
                )
            }
            if (state.isLoading || state.isUploading) {
                CircularProgressIndicator(Modifier.align(Alignment.Center))
            }
        }
    }

    if (showCreate) {
        TextInputDialog(
            title = stringResource(R.string.files_dialog_new_folder_title),
            label = stringResource(R.string.files_dialog_new_folder_hint),
            confirmText = stringResource(R.string.files_dialog_create),
            onConfirm = { name -> showCreate = false; viewModel.createFolder(name) },
            onDismiss = { showCreate = false },
        )
    }
    renameTarget?.let { target ->
        TextInputDialog(
            title = stringResource(R.string.files_dialog_rename_title),
            label = stringResource(R.string.files_dialog_rename_hint),
            confirmText = stringResource(R.string.common_save),
            initial = when (target) {
                is CloudTarget.Dir -> target.name
                is CloudTarget.File -> target.name
            },
            onConfirm = { name ->
                renameTarget = null
                when (target) {
                    is CloudTarget.Dir -> viewModel.renameDirectory(target.id, name)
                    is CloudTarget.File -> viewModel.renameFile(target.entryId, name)
                }
            },
            onDismiss = { renameTarget = null },
        )
    }
    deleteTarget?.let { target ->
        val name = when (target) { is CloudTarget.Dir -> target.name; is CloudTarget.File -> target.name }
        AlertDialog(
            onDismissRequest = { deleteTarget = null },
            title = { Text(stringResource(R.string.files_dialog_delete_title)) },
            text = { Text(name) },
            confirmButton = {
                TextButton(onClick = {
                    deleteTarget = null
                    when (target) {
                        is CloudTarget.Dir -> viewModel.deleteDirectory(target.id)
                        is CloudTarget.File -> viewModel.deleteFile(target.entryId)
                    }
                }) { Text(stringResource(R.string.common_delete), color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = {
                TextButton(onClick = { deleteTarget = null }) { Text(stringResource(R.string.common_cancel)) }
            },
        )
    }
    moveTarget?.let { target ->
        CloudMovePicker(
            excludeId = (target as? CloudTarget.Dir)?.id,
            onPicked = { dest ->
                moveTarget = null
                when (target) {
                    is CloudTarget.Dir -> viewModel.moveDirectory(target.id, dest)
                    is CloudTarget.File -> viewModel.moveFile(target.entryId, dest)
                }
            },
            onDismiss = { moveTarget = null },
        )
    }
}

@Composable
private fun Breadcrumbs(crumbs: List<Pair<String, String>>, onOpen: (String, String) -> Unit) {
    Row(
        modifier = Modifier
            .horizontalScroll(rememberScrollState())
            .padding(horizontal = 16.dp, vertical = 8.dp),
    ) {
        crumbs.forEachIndexed { index, (id, name) ->
            Text(
                text = name,
                style = MaterialTheme.typography.labelLarge,
                fontWeight = if (index == crumbs.lastIndex) FontWeight.Bold else FontWeight.Normal,
                color = MaterialTheme.colorScheme.primary,
                modifier = Modifier.clickable { onOpen(id, name) }.padding(end = 4.dp),
            )
            if (index != crumbs.lastIndex) Text(" / ", color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@Composable
private fun FileRow(
    file: CloudFileEntry,
    onOpen: () -> Unit,
    onRename: () -> Unit,
    onMove: () -> Unit,
    onDelete: () -> Unit,
) {
    val hasPreview = file.asset.previews.isNotEmpty()
    ListItem(
        headlineContent = { Text(file.name, maxLines = 1) },
        leadingContent = {
            if (hasPreview) {
                MediaThumb(
                    model = file.asset.previewUrl(128),
                    isVideo = file.asset.isVideo,
                    contentDescription = file.name,
                    modifier = Modifier.size(40.dp).clip(RoundedCornerShape(8.dp)),
                )
            } else {
                Icon(MimeIcon.iconFor(MimeIcon.mimeFor(file.name), file.name), contentDescription = null)
            }
        },
        trailingContent = {
            ItemMenu(onRename = onRename, onMove = onMove, onDelete = onDelete)
        },
        modifier = Modifier.clickable(onClick = onOpen),
    )
}

@Composable
private fun ItemMenu(
    onRename: () -> Unit,
    onMove: () -> Unit,
    onDelete: () -> Unit,
) {
    var open by remember { mutableStateOf(false) }
    Box {
        IconButton(onClick = { open = true }) {
            Icon(Icons.Outlined.MoreVert, contentDescription = null)
        }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            DropdownMenuItem(
                text = { Text(stringResource(R.string.files_action_rename)) },
                leadingIcon = { Icon(Icons.Outlined.Edit, contentDescription = null) },
                onClick = { open = false; onRename() },
            )
            DropdownMenuItem(
                text = { Text(stringResource(R.string.files_action_move)) },
                leadingIcon = { Icon(Icons.Outlined.DriveFileMove, contentDescription = null) },
                onClick = { open = false; onMove() },
            )
            DropdownMenuItem(
                text = { Text(stringResource(R.string.files_action_delete)) },
                leadingIcon = { Icon(Icons.Outlined.Delete, contentDescription = null) },
                onClick = { open = false; onDelete() },
            )
        }
    }
}
