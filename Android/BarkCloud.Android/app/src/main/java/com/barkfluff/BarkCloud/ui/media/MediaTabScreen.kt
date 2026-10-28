package com.barkfluff.BarkCloud.ui.media

import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.FavoriteBorder
import androidx.compose.material3.CenterAlignedTopAppBar
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.outlined.DeleteOutline
import androidx.compose.material.icons.outlined.PeopleOutline
import androidx.compose.material.icons.outlined.PhoneAndroid
import androidx.compose.ui.Alignment
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.unit.dp
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
    onOpenAlbum: (id: String, name: String) -> Unit,
    onOpenFavorites: () -> Unit,
    onOpenTrash: () -> Unit,
    onOpenDevice: () -> Unit,
) {
    val snackbarHostState = remember { SnackbarHostState() }
    val scope = rememberCoroutineScope()
    val onSnackbar: (String) -> Unit = { msg -> scope.launch { snackbarHostState.showSnackbar(msg) } }

    Scaffold(
        contentWindowInsets = WindowInsets(0, 0, 0, 0),
        snackbarHost = { SnackbarHost(snackbarHostState) },
    ) { padding ->
        androidx.compose.foundation.layout.Box(Modifier.fillMaxSize().padding(padding)) {
            AlbumsGridScreen(
                onOpenAlbum = onOpenAlbum,
                onSnackbar = onSnackbar,
                shortcuts = {
                    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                            CollectionShortcut(stringResource(R.string.tab_favorites), Icons.Outlined.FavoriteBorder, onOpenFavorites, Modifier.weight(1f))
                            CollectionShortcut(stringResource(R.string.tab_trash), Icons.Outlined.DeleteOutline, onOpenTrash, Modifier.weight(1f))
                        }
                        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                            CollectionShortcut(stringResource(R.string.collections_people), Icons.Outlined.PeopleOutline, {}, Modifier.weight(1f), enabled = false)
                            CollectionShortcut(stringResource(R.string.collections_device), Icons.Outlined.PhoneAndroid, onOpenDevice, Modifier.weight(1f))
                        }
                    }
                },
                header = {
                    CenterAlignedTopAppBar(
                        title = { Text(stringResource(R.string.tab_albums)) },
                        windowInsets = WindowInsets(0, 0, 0, 0),
                    )
                },
            )
        }
    }
}

@Composable
private fun CollectionShortcut(
    label: String,
    icon: ImageVector,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
) {
    FilledTonalButton(
        onClick = onClick,
        enabled = enabled,
        shape = CircleShape,
        modifier = modifier.heightIn(min = 64.dp),
        contentPadding = PaddingValues(horizontal = 16.dp, vertical = 16.dp),
    ) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Icon(icon, contentDescription = null, modifier = Modifier.size(24.dp))
            Text(label, style = MaterialTheme.typography.titleSmall)
        }
    }
}
