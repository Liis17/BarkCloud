package com.barkfluff.BarkCloud.ui.components

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cloud.AlbumCard
import com.barkfluff.BarkCloud.data.cloud.AlbumRepository

/** Все альбомы владельца (страницами до конца списка) для выбора назначения. */
internal suspend fun loadAllAlbums(albums: AlbumRepository): List<AlbumCard> {
    val result = mutableListOf<AlbumCard>()
    var page = albums.listAlbums()
    result.addAll(page.albums)
    while (page.hasMore) {
        page = albums.listAlbums(cursorUpdatedAtMillis = page.nextCursorUpdatedAtMillis, cursorAlbumId = page.nextCursorAlbumId)
        result.addAll(page.albums)
    }
    return result
}

/** Выбор альбома назначения. [enabled] блокирует выбор, пока выполняется операция. */
@Composable
internal fun CloudAlbumPickerDialog(
    albums: List<AlbumCard>,
    enabled: Boolean,
    onPick: (AlbumCard) -> Unit,
    onDismiss: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.viewer_action_album)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState())) {
                if (albums.isEmpty()) Text(stringResource(R.string.viewer_albums_empty))
                albums.forEach { album ->
                    TextButton(enabled = enabled, onClick = { onPick(album) }) { Text(album.name) }
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.viewer_action_cancel)) } },
    )
}
