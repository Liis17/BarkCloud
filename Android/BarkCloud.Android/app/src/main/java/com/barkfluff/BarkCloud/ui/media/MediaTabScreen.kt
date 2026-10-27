package com.barkfluff.BarkCloud.ui.media

import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.FavoriteBorder
import androidx.compose.material3.CenterAlignedTopAppBar
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.ui.albums.AlbumsGridScreen
import kotlinx.coroutines.launch

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MediaTabScreen(
    menu: @Composable () -> Unit = {},
    onOpenAlbum: (id: String, name: String) -> Unit,
    onOpenFavorites: () -> Unit,
) {
    val snackbarHostState = remember { SnackbarHostState() }
    val scope = rememberCoroutineScope()
    val onSnackbar: (String) -> Unit = { msg -> scope.launch { snackbarHostState.showSnackbar(msg) } }

    Scaffold(
        topBar = {
            CenterAlignedTopAppBar(
                title = { Text(stringResource(R.string.tab_albums)) },
                actions = {
                    menu()
                    IconButton(onClick = onOpenFavorites) {
                        Icon(Icons.Outlined.FavoriteBorder, contentDescription = stringResource(R.string.tab_favorites))
                    }
                },
            )
        },
        snackbarHost = { SnackbarHost(snackbarHostState) },
    ) { padding ->
        androidx.compose.foundation.layout.Box(Modifier.fillMaxSize().padding(padding)) {
            AlbumsGridScreen(onOpenAlbum = onOpenAlbum, onSnackbar = onSnackbar)
        }
    }
}
