package com.barkfluff.BarkCloud.ui.media

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.FavoriteBorder
import androidx.compose.material3.CenterAlignedTopAppBar
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cloud.CloudMediaKind
import com.barkfluff.BarkCloud.ui.albums.AlbumsGridScreen
import kotlinx.coroutines.launch

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MediaTabScreen(
    onOpenAlbum: (id: String, name: String) -> Unit,
    onOpenFavorites: () -> Unit,
) {
    var segment by rememberSaveable { mutableIntStateOf(0) }
    val snackbarHostState = remember { SnackbarHostState() }
    val scope = rememberCoroutineScope()
    val onSnackbar: (String) -> Unit = { msg -> scope.launch { snackbarHostState.showSnackbar(msg) } }

    val segments = listOf(
        stringResource(R.string.media_segment_photos),
        stringResource(R.string.media_segment_videos),
        stringResource(R.string.media_segment_albums),
    )

    Scaffold(
        topBar = {
            CenterAlignedTopAppBar(
                title = { Text(stringResource(R.string.tab_albums)) },
                actions = {
                    IconButton(onClick = onOpenFavorites) {
                        Icon(Icons.Outlined.FavoriteBorder, contentDescription = stringResource(R.string.tab_favorites))
                    }
                },
            )
        },
        snackbarHost = { SnackbarHost(snackbarHostState) },
    ) { padding ->
        Column(Modifier.fillMaxSize().padding(padding)) {
            SingleChoiceSegmentedButtonRow(
                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
            ) {
                segments.forEachIndexed { index, label ->
                    SegmentedButton(
                        selected = segment == index,
                        onClick = { segment = index },
                        shape = SegmentedButtonDefaults.itemShape(index, segments.size),
                    ) {
                        Text(label)
                    }
                }
            }

            when (segment) {
                0 -> MediaGridScreen(kind = CloudMediaKind.PHOTO, onSnackbar = onSnackbar)
                1 -> MediaGridScreen(kind = CloudMediaKind.VIDEO, onSnackbar = onSnackbar)
                else -> AlbumsGridScreen(onOpenAlbum = onOpenAlbum, onSnackbar = onSnackbar)
            }
        }
    }
}
