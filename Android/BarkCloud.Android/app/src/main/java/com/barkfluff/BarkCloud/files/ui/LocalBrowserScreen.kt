package com.barkfluff.BarkCloud.files.ui

import android.content.ActivityNotFoundException
import android.content.Intent
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.automirrored.outlined.DriveFileMove
import androidx.compose.material.icons.automirrored.outlined.Sort
import androidx.compose.material.icons.outlined.Close
import androidx.compose.material.icons.outlined.ContentCopy
import androidx.compose.material.icons.outlined.CreateNewFolder
import androidx.compose.material.icons.outlined.Delete
import androidx.compose.material.icons.outlined.DriveFileRenameOutline
import androidx.compose.material.icons.outlined.FolderOpen
import androidx.compose.material.icons.outlined.MoreVert
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CenterAlignedTopAppBar
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.files.data.FileShareHelper
import com.barkfluff.BarkCloud.files.domain.FsEntry
import com.barkfluff.BarkCloud.files.domain.FsSort

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun LocalBrowserScreen(
    initialPath: String,
    onNavigateUp: () -> Unit,
) {
    val context = LocalContext.current
    val rootLabel = stringResource(R.string.files_storage_root)
    val viewModel: LocalBrowserViewModel = viewModel(
        key = "browser:$initialPath",
        factory = LocalBrowserViewModel.factory(initialPath, rootLabel),
    )
    val state by viewModel.state.collectAsState()
    val snackbarHost = remember { SnackbarHostState() }

    val errorNoApp = stringResource(R.string.files_op_error_no_app)

    LaunchedEffect(Unit) {
        viewModel.events.collect { event ->
            when (event) {
                is LocalBrowserViewModel.BrowserEvent.Toast -> snackbarHost.showSnackbar(event.text)
                is LocalBrowserViewModel.BrowserEvent.OpenFile -> {
                    val intent = FileShareHelper.buildOpenIntent(context, event.file, event.mime)
                    try {
                        context.startActivity(intent)
                    } catch (_: ActivityNotFoundException) {
                        snackbarHost.showSnackbar(errorNoApp)
                    }
                }
                is LocalBrowserViewModel.BrowserEvent.ShareFiles -> {
                    val intent = FileShareHelper.buildShareIntent(context, event.files)
                    try {
                        context.startActivity(Intent.createChooser(intent, null))
                    } catch (_: ActivityNotFoundException) {
                        snackbarHost.showSnackbar(errorNoApp)
                    }
                }
            }
        }
    }

    BackHandler(enabled = state.selectionActive || state.canGoUp) {
        if (state.selectionActive) {
            viewModel.clearSelection()
        } else if (state.canGoUp) {
            viewModel.goUp()
        } else {
            onNavigateUp()
        }
    }

    var showNewFolderDialog by remember { mutableStateOf(false) }
    var renameTarget by remember { mutableStateOf<FsEntry?>(null) }
    var showDeleteDialog by remember { mutableStateOf(false) }
    var copyMode by remember { mutableStateOf<CopyOrMove?>(null) }
    var sortMenuOpen by remember { mutableStateOf(false) }
    var overflowOpen by remember { mutableStateOf(false) }

    Scaffold(
        topBar = {
            if (state.selectionActive) {
                ContextualBar(
                    selectionCount = state.selection.size,
                    canRename = state.selection.size == 1,
                    onClear = { viewModel.clearSelection() },
                    onDelete = { showDeleteDialog = true },
                    onCopy = { copyMode = CopyOrMove.Copy },
                    onMove = { copyMode = CopyOrMove.Move },
                    onRename = {
                        val entry = viewModel.selectedEntries().firstOrNull() ?: return@ContextualBar
                        renameTarget = entry
                    },
                )
            } else {
                CenterAlignedTopAppBar(
                    title = { Text(state.title) },
                    navigationIcon = {
                        IconButton(onClick = {
                            if (state.canGoUp) viewModel.goUp() else onNavigateUp()
                        }) {
                            Icon(
                                imageVector = Icons.AutoMirrored.Outlined.ArrowBack,
                                contentDescription = stringResource(R.string.files_back),
                            )
                        }
                    },
                    actions = {
                        IconButton(onClick = { sortMenuOpen = true }) {
                            Icon(
                                imageVector = Icons.AutoMirrored.Outlined.Sort,
                                contentDescription = stringResource(R.string.files_action_sort),
                            )
                        }
                        DropdownMenu(
                            expanded = sortMenuOpen,
                            onDismissRequest = { sortMenuOpen = false },
                        ) {
                            sortOptions().forEach { (sort, labelRes) ->
                                DropdownMenuItem(
                                    text = { Text(stringResource(labelRes)) },
                                    onClick = {
                                        sortMenuOpen = false
                                        viewModel.setSort(sort)
                                    },
                                )
                            }
                        }
                        IconButton(onClick = { overflowOpen = true }) {
                            Icon(
                                imageVector = Icons.Outlined.MoreVert,
                                contentDescription = stringResource(R.string.files_action_more),
                            )
                        }
                        DropdownMenu(
                            expanded = overflowOpen,
                            onDismissRequest = { overflowOpen = false },
                        ) {
                            DropdownMenuItem(
                                text = { Text(stringResource(R.string.files_action_select_all)) },
                                onClick = { overflowOpen = false; viewModel.selectAll() },
                            )
                            DropdownMenuItem(
                                text = {
                                    Text(
                                        stringResource(
                                            if (state.showHidden) R.string.files_action_hide_hidden
                                            else R.string.files_action_show_hidden,
                                        ),
                                    )
                                },
                                onClick = { overflowOpen = false; viewModel.toggleHidden() },
                            )
                        }
                    },
                )
            }
        },
        snackbarHost = { SnackbarHost(snackbarHost) },
        floatingActionButton = {
            if (!state.selectionActive) {
                ExtendedFloatingActionButton(
                    onClick = { showNewFolderDialog = true },
                    icon = {
                        Icon(
                            imageVector = Icons.Outlined.CreateNewFolder,
                            contentDescription = null,
                        )
                    },
                    text = { Text(stringResource(R.string.files_action_create_folder)) },
                )
            }
        },
    ) { padding ->
        Box(modifier = Modifier.fillMaxSize().padding(padding)) {
            if (state.entries.isEmpty() && !state.isLoading) {
                Column(
                    modifier = Modifier.fillMaxSize(),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.Center,
                ) {
                    Icon(
                        imageVector = Icons.Outlined.FolderOpen,
                        contentDescription = null,
                        tint = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.size(56.dp),
                    )
                    Spacer(modifier = Modifier.height(12.dp))
                    Text(
                        text = stringResource(R.string.files_empty_folder),
                        style = MaterialTheme.typography.bodyLarge,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        textAlign = TextAlign.Center,
                    )
                }
            }

            LazyColumn(
                contentPadding = PaddingValues(vertical = 8.dp, horizontal = 8.dp),
            ) {
                items(state.entries, key = { it.path }) { entry ->
                    FsRowItem(
                        entry = entry,
                        selected = entry.path in state.selection,
                        selectionActive = state.selectionActive,
                        onPrimaryClick = {
                            if (state.selectionActive) {
                                viewModel.toggleSelect(entry.path)
                            } else when (entry) {
                                is FsEntry.Directory -> viewModel.enter(entry)
                                is FsEntry.File -> viewModel.openFile(entry)
                            }
                        },
                        onLongPress = { viewModel.toggleSelect(entry.path) },
                        onLeadingClick = { viewModel.toggleSelect(entry.path) },
                        onActionOpen = {
                            when (entry) {
                                is FsEntry.Directory -> viewModel.enter(entry)
                                is FsEntry.File -> viewModel.openFile(entry)
                            }
                        },
                        onActionShare = {
                            if (entry is FsEntry.File) viewModel.shareFiles(listOf(entry))
                        },
                        onActionRename = { renameTarget = entry },
                        onActionCopy = {
                            viewModel.clearSelection()
                            viewModel.toggleSelect(entry.path)
                            copyMode = CopyOrMove.Copy
                        },
                        onActionMove = {
                            viewModel.clearSelection()
                            viewModel.toggleSelect(entry.path)
                            copyMode = CopyOrMove.Move
                        },
                        onActionDelete = {
                            viewModel.clearSelection()
                            viewModel.toggleSelect(entry.path)
                            showDeleteDialog = true
                        },
                    )
                }
            }

            state.pendingOp?.let { op ->
                LinearProgressIndicator(
                    progress = { op.progress },
                    modifier = Modifier
                        .align(Alignment.TopCenter)
                        .fillMaxWidth(),
                )
            }
        }
    }

    if (showNewFolderDialog) {
        TextInputDialog(
            title = stringResource(R.string.files_dialog_new_folder_title),
            hint = stringResource(R.string.files_dialog_new_folder_hint),
            initial = "",
            confirmLabel = stringResource(R.string.files_dialog_create),
            onDismiss = { showNewFolderDialog = false },
            onConfirm = { name ->
                showNewFolderDialog = false
                viewModel.createFolder(name)
            },
        )
    }

    renameTarget?.let { entry ->
        TextInputDialog(
            title = stringResource(R.string.files_dialog_rename_title),
            hint = stringResource(R.string.files_dialog_rename_hint),
            initial = entry.name,
            confirmLabel = stringResource(R.string.files_dialog_confirm),
            onDismiss = { renameTarget = null },
            onConfirm = { name ->
                renameTarget = null
                viewModel.rename(entry, name)
            },
        )
    }

    if (showDeleteDialog) {
        AlertDialog(
            onDismissRequest = { showDeleteDialog = false },
            title = { Text(stringResource(R.string.files_dialog_delete_title)) },
            text = {
                Text(stringResource(R.string.files_dialog_delete_message, state.selection.size))
            },
            confirmButton = {
                Button(onClick = {
                    showDeleteDialog = false
                    viewModel.deleteSelected()
                }) {
                    Text(stringResource(R.string.files_dialog_delete_confirm))
                }
            },
            dismissButton = {
                TextButton(onClick = { showDeleteDialog = false }) {
                    Text(stringResource(R.string.files_dialog_cancel))
                }
            },
        )
    }

    val mode = copyMode
    if (mode != null) {
        val selectedPaths = state.selection
        val forbidden = remember(selectedPaths) {
            // Запрещаем выбрать саму выделенную папку как назначение.
            selectedPaths.toSet()
        }
        PickFolderDialog(
            forbiddenPaths = forbidden,
            onDismiss = { copyMode = null },
            onConfirm = { target ->
                copyMode = null
                when (mode) {
                    CopyOrMove.Copy -> viewModel.copySelectedTo(target)
                    CopyOrMove.Move -> viewModel.moveSelectedTo(target)
                }
            },
        )
    }
}

private enum class CopyOrMove { Copy, Move }

private fun sortOptions(): List<Pair<FsSort, Int>> = listOf(
    FsSort.NameAsc to R.string.files_sort_name_asc,
    FsSort.NameDesc to R.string.files_sort_name_desc,
    FsSort.SizeAsc to R.string.files_sort_size_asc,
    FsSort.SizeDesc to R.string.files_sort_size_desc,
    FsSort.DateAsc to R.string.files_sort_date_asc,
    FsSort.DateDesc to R.string.files_sort_date_desc,
)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ContextualBar(
    selectionCount: Int,
    canRename: Boolean,
    onClear: () -> Unit,
    onDelete: () -> Unit,
    onCopy: () -> Unit,
    onMove: () -> Unit,
    onRename: () -> Unit,
) {
    TopAppBar(
        title = { Text("$selectionCount") },
        navigationIcon = {
            IconButton(onClick = onClear) {
                Icon(
                    imageVector = Icons.Outlined.Close,
                    contentDescription = stringResource(R.string.files_action_clear_selection),
                )
            }
        },
        actions = {
            if (canRename) {
                IconButton(onClick = onRename) {
                    Icon(
                        imageVector = Icons.Outlined.DriveFileRenameOutline,
                        contentDescription = stringResource(R.string.files_action_rename),
                    )
                }
            }
            IconButton(onClick = onCopy) {
                Icon(
                    imageVector = Icons.Outlined.ContentCopy,
                    contentDescription = stringResource(R.string.files_action_copy),
                )
            }
            IconButton(onClick = onMove) {
                Icon(
                    imageVector = Icons.AutoMirrored.Outlined.DriveFileMove,
                    contentDescription = stringResource(R.string.files_action_move),
                )
            }
            IconButton(onClick = onDelete) {
                Icon(
                    imageVector = Icons.Outlined.Delete,
                    contentDescription = stringResource(R.string.files_action_delete),
                )
            }
        },
    )
}

@Composable
private fun TextInputDialog(
    title: String,
    hint: String,
    initial: String,
    confirmLabel: String,
    onDismiss: () -> Unit,
    onConfirm: (String) -> Unit,
) {
    var value by remember { mutableStateOf(initial) }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = {
            OutlinedTextField(
                value = value,
                onValueChange = { value = it },
                singleLine = true,
                label = { Text(hint) },
            )
        },
        confirmButton = {
            Button(
                onClick = { onConfirm(value.trim()) },
                enabled = value.isNotBlank() && !value.contains('/') && !value.contains('\\'),
            ) {
                Text(confirmLabel)
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) {
                Text(stringResource(R.string.files_dialog_cancel))
            }
        },
    )
}
