package com.barkfluff.BarkCloud.ui.components

import android.content.Intent
import android.net.Uri
import android.webkit.WebView
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Close
import androidx.compose.material3.AssistChip
import androidx.compose.material3.Button
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.InputChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cloud.MediaAsset
import com.barkfluff.BarkCloud.files.ui.formatSize

private const val MAX_TAGS = 20
private const val MAX_ALIAS_LENGTH = 120

/** Содержимое панели свойств: имя, размер, дата, поиск и теги, метаданные и карта места съёмки. */
@Composable
internal fun CloudMediaInfoContent(
    asset: MediaAsset,
    state: CloudMediaInfoUiState,
    onSave: (alias: String, tags: List<String>) -> Unit,
) {
    val context = LocalContext.current
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 16.dp, vertical = 8.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text(
            text = asset.fileName,
            style = MaterialTheme.typography.titleMedium,
            maxLines = 2,
            overflow = TextOverflow.Ellipsis,
        )
        InfoRow(stringResource(R.string.viewer_info_size), formatSize(context, asset.fileSize))
        InfoRow(stringResource(R.string.viewer_info_date), formatTimestamp(asset.createdAtMillis / 1000))

        // Черновик поиска и тегов сбрасывается при смене файла, поэтому ключ — id файла.
        key(asset.id) { SearchEditor(state = state, onSave = onSave) }

        if (state.isLoading) {
            Text(stringResource(R.string.viewer_info_loading), color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        state.metadataError?.let {
            Text(
                stringResource(R.string.viewer_info_metadata_error, it),
                color = MaterialTheme.colorScheme.error,
            )
        }
        state.metadataRows.forEach { (label, value) -> InfoRow(label, value) }

        state.coordinates?.let { (lat, lon) ->
            Text(stringResource(R.string.viewer_info_map_title), style = MaterialTheme.typography.titleSmall)
            // key пересоздаёт WebView при смене точки, иначе карта останется от прошлого файла.
            key(lat, lon) {
                AndroidView(
                    factory = { ctx ->
                        WebView(ctx).apply {
                            settings.javaScriptEnabled = true
                            // Карта только для просмотра: жесты не должны листать панель. Открытие — по кнопке.
                            setOnTouchListener { _, _ -> true }
                            loadUrl(osmEmbedUrl(lat, lon))
                        }
                    },
                    modifier = Modifier.fillMaxWidth().height(200.dp),
                )
            }
            TextButton(onClick = {
                context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(osmPageUrl(lat, lon))))
            }) {
                Text(stringResource(R.string.viewer_info_map_open))
            }
        }
    }
}

/** Блоки «Имя для поиска» и «Теги»: черновик локально, сохранение отправляет alias.trim() и теги. */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun SearchEditor(
    state: CloudMediaInfoUiState,
    onSave: (alias: String, tags: List<String>) -> Unit,
) {
    var alias by remember(state.alias) { mutableStateOf(state.alias) }
    var tags by remember(state.tags) { mutableStateOf(state.tags) }
    var menuOpen by remember { mutableStateOf(false) }
    val changed = alias.trim() != state.alias || tags != state.tags

    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text(stringResource(R.string.viewer_info_search_title), style = MaterialTheme.typography.titleSmall)
        OutlinedTextField(
            value = alias,
            onValueChange = { if (it.length <= MAX_ALIAS_LENGTH) alias = it },
            modifier = Modifier.fillMaxWidth(),
            singleLine = true,
            enabled = !state.saving,
            supportingText = { Text(stringResource(R.string.viewer_info_search_help)) },
        )

        Text(stringResource(R.string.viewer_info_tags_title), style = MaterialTheme.typography.titleSmall)
        FlowRow(
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            tags.forEach { name ->
                InputChip(
                    selected = false,
                    onClick = { tags = tags - name },
                    enabled = !state.saving,
                    label = { Text(name) },
                    trailingIcon = { Icon(Icons.Filled.Close, contentDescription = null, modifier = Modifier.size(18.dp)) },
                )
            }
            Box {
                AssistChip(
                    onClick = { menuOpen = true },
                    enabled = !state.saving && tags.size < MAX_TAGS,
                    label = { Text(stringResource(R.string.viewer_info_tag_add)) },
                    leadingIcon = { Icon(Icons.Filled.Add, contentDescription = null, modifier = Modifier.size(18.dp)) },
                )
                DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                    val available = state.catalog.filter { it.name !in tags }
                    if (available.isEmpty()) {
                        DropdownMenuItem(
                            text = { Text(stringResource(R.string.viewer_info_tags_empty)) },
                            onClick = { menuOpen = false },
                            enabled = false,
                        )
                    }
                    available.forEach { tag ->
                        DropdownMenuItem(
                            text = { Text(tag.name) },
                            onClick = {
                                tags = tags + tag.name
                                menuOpen = false
                            },
                        )
                    }
                }
            }
        }
        if (tags.size >= MAX_TAGS) {
            Text(stringResource(R.string.viewer_info_tags_limit), style = MaterialTheme.typography.bodySmall)
        }

        state.searchError?.let {
            Text(stringResource(R.string.viewer_info_search_error, it), color = MaterialTheme.colorScheme.error)
        }
        state.saveError?.let {
            Text(stringResource(R.string.viewer_info_save_error, it), color = MaterialTheme.colorScheme.error)
        }
        Button(
            onClick = { onSave(alias.trim(), tags) },
            enabled = changed && !state.saving,
        ) {
            Text(
                stringResource(if (state.saving) R.string.viewer_info_saving else R.string.viewer_info_save),
            )
        }
    }
}

@Composable
private fun InfoRow(label: String, value: String) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.Top,
    ) {
        Text(label, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(
            value,
            modifier = Modifier.padding(start = 16.dp).weight(1f, fill = false),
            textAlign = TextAlign.End,
        )
    }
}
