package com.barkfluff.BarkCloud.ui.components

import android.content.Intent
import android.net.Uri
import android.webkit.WebView
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.key
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

/** Содержимое панели свойств: имя, размер, дата, метаданные и карта места съёмки. */
@Composable
internal fun CloudMediaInfoContent(
    asset: MediaAsset,
    state: CloudMediaInfoUiState,
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
