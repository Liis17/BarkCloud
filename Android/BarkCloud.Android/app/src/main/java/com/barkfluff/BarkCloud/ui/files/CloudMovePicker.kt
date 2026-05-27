package com.barkfluff.BarkCloud.ui.files

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.Close
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cloud.CloudDirectory

/**
 * Диалог выбора целевой папки в облаке: навигация по дереву + кнопка «Переместить сюда».
 * [excludeId] исключает саму перемещаемую папку (нельзя положить в себя).
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun CloudMovePicker(
    excludeId: String?,
    onPicked: (targetDirectoryId: String) -> Unit,
    onDismiss: () -> Unit,
) {
    val app = LocalContext.current.applicationContext as BarkCloudApplication
    var stack by remember { mutableStateOf<List<CloudDirectory>>(emptyList()) }
    var subdirs by remember { mutableStateOf<List<CloudDirectory>>(emptyList()) }
    val currentId = stack.lastOrNull()?.id ?: ""

    androidx.compose.runtime.LaunchedEffect(currentId) {
        subdirs = runCatching { app.cloudRepository.listDirectory(currentId).subdirs }
            .getOrDefault(emptyList())
            .filter { it.id != excludeId }
    }

    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Surface(Modifier.fillMaxSize()) {
            Scaffold(
                topBar = {
                    TopAppBar(
                        title = { Text(stack.lastOrNull()?.name ?: stringResource(R.string.files_pick_folder_title)) },
                        navigationIcon = {
                            if (stack.isEmpty()) {
                                IconButton(onClick = onDismiss) { Icon(Icons.Outlined.Close, contentDescription = null) }
                            } else {
                                IconButton(onClick = { stack = stack.dropLast(1) }) {
                                    Icon(Icons.AutoMirrored.Outlined.ArrowBack, contentDescription = null)
                                }
                            }
                        },
                    )
                },
                bottomBar = {
                    Row(
                        modifier = Modifier.fillMaxWidth().padding(16.dp),
                        horizontalArrangement = Arrangement.spacedBy(12.dp),
                    ) {
                        TextButton(onClick = onDismiss, modifier = Modifier.weight(1f)) {
                            Text(stringResource(R.string.common_cancel))
                        }
                        Button(onClick = { onPicked(currentId) }, modifier = Modifier.weight(1f)) {
                            Text(stringResource(R.string.files_pick_folder_confirm))
                        }
                    }
                },
            ) { padding ->
                LazyColumn(Modifier.fillMaxSize().padding(padding)) {
                    items(subdirs, key = { it.id }) { dir ->
                        ListItem(
                            headlineContent = { Text(dir.name) },
                            leadingContent = { Icon(Icons.Outlined.Folder, contentDescription = null) },
                            modifier = Modifier.clickable { stack = stack + dir },
                        )
                    }
                }
            }
        }
    }
}
