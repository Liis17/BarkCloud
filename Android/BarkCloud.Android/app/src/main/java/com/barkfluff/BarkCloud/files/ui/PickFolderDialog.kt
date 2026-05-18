package com.barkfluff.BarkCloud.files.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.files.data.StoragePermission
import com.barkfluff.BarkCloud.files.domain.FsEntry
import com.barkfluff.BarkCloud.files.domain.applySort
import com.barkfluff.BarkCloud.files.domain.FsSort
import androidx.compose.ui.platform.LocalContext
import kotlinx.coroutines.flow.MutableStateFlow

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PickFolderDialog(
    forbiddenPaths: Set<String>,
    onDismiss: () -> Unit,
    onConfirm: (String) -> Unit,
) {
    val context = LocalContext.current
    val app = context.applicationContext as BarkCloudApplication
    val repository = app.localFileRepository
    val rootPath = remember { StoragePermission.externalRoot.absolutePath }

    var currentPath by remember { mutableStateOf(rootPath) }
    val entriesState = remember { MutableStateFlow<List<FsEntry>>(emptyList()) }
    val entries by entriesState.collectAsState()

    LaunchedEffect(currentPath) {
        val result = repository.list(currentPath, includeHidden = false)
        entriesState.value = result.getOrDefault(emptyList())
            .filter { it is FsEntry.Directory }
            .applySort(FsSort.NameAsc)
    }

    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    val scope = rememberCoroutineScope()

    val isAtRoot = currentPath == rootPath
    val isForbidden = currentPath in forbiddenPaths

    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = sheetState,
    ) {
        Column(modifier = Modifier.padding(bottom = 8.dp)) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(start = 8.dp, end = 16.dp),
            ) {
                IconButton(
                    onClick = {
                        currentPath = java.io.File(currentPath).parentFile?.absolutePath ?: rootPath
                    },
                    enabled = !isAtRoot,
                ) {
                    Icon(
                        imageVector = Icons.AutoMirrored.Outlined.ArrowBack,
                        contentDescription = stringResource(R.string.files_back),
                    )
                }
                Spacer(modifier = Modifier.width(8.dp))
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        text = stringResource(R.string.files_pick_folder_title),
                        style = MaterialTheme.typography.titleMedium,
                    )
                    Text(
                        text = displayName(currentPath, rootPath, context.getString(R.string.files_storage_root)),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }

            HorizontalDivider()

            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(min = 200.dp, max = 480.dp),
            ) {
                LazyColumn(contentPadding = PaddingValues(vertical = 4.dp)) {
                    items(entries, key = { it.path }) { entry ->
                        Row(
                            verticalAlignment = Alignment.CenterVertically,
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(horizontal = 16.dp, vertical = 10.dp),
                        ) {
                            Icon(
                                imageVector = Icons.Outlined.Folder,
                                contentDescription = null,
                                tint = MaterialTheme.colorScheme.primary,
                                modifier = Modifier.size(24.dp),
                            )
                            Spacer(modifier = Modifier.width(12.dp))
                            Text(
                                text = entry.name,
                                style = MaterialTheme.typography.bodyLarge,
                                modifier = Modifier.weight(1f),
                            )
                            TextButton(onClick = { currentPath = entry.path }) {
                                Text(stringResource(R.string.files_action_open))
                            }
                        }
                    }
                }
            }

            HorizontalDivider()

            Row(
                horizontalArrangement = Arrangement.End,
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 16.dp, vertical = 12.dp),
            ) {
                TextButton(onClick = onDismiss) {
                    Text(stringResource(R.string.files_dialog_cancel))
                }
                Spacer(modifier = Modifier.width(8.dp))
                Button(
                    onClick = { onConfirm(currentPath) },
                    enabled = !isForbidden,
                ) {
                    Text(stringResource(R.string.files_pick_folder_confirm))
                }
            }
        }
    }

    // suppress unused warning
    scope.toString()
}

private fun displayName(path: String, rootPath: String, rootLabel: String): String {
    if (path == rootPath) return rootLabel
    val rootPrefix = "$rootPath/"
    return if (path.startsWith(rootPrefix)) "$rootLabel/" + path.substring(rootPrefix.length)
    else path
}
