package com.barkfluff.BarkCloud.ui.media

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.outlined.RadioButtonUnchecked
import androidx.compose.material.icons.outlined.FavoriteBorder
import androidx.compose.material.icons.outlined.VisibilityOff
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cloud.MediaAsset
import com.barkfluff.BarkCloud.ui.components.MediaThumb

@OptIn(ExperimentalFoundationApi::class)
@Composable
internal fun MediaCell(
    asset: MediaAsset,
    onTap: () -> Unit,
    onFavorite: () -> Unit,
    onAddToVault: () -> Unit,
    onLongPress: (() -> Unit)? = null,
    selecting: Boolean = false,
    selected: Boolean = false,
) {
    var menuOpen by remember { mutableStateOf(false) }
    Box(
        modifier = Modifier
            .aspectRatio(1f)
            .padding(1.dp)
            .clip(MaterialTheme.shapes.small)
            .combinedClickable(
                onClick = onTap,
                onLongClick = { if (onLongPress != null) onLongPress() else menuOpen = true },
            ),
    ) {
        MediaThumb(
            model = asset.previewUrl(512),
            isVideo = asset.isVideo,
            contentDescription = asset.fileName,
            modifier = Modifier.fillMaxSize(),
        )
        if (selecting) {
            Box(
                Modifier.fillMaxSize().background(MaterialTheme.colorScheme.primary.copy(alpha = if (selected) 0.25f else 0f)),
            )
            Icon(
                if (selected) Icons.Filled.CheckCircle else Icons.Outlined.RadioButtonUnchecked,
                contentDescription = null,
                tint = if (selected) MaterialTheme.colorScheme.primary else Color.White,
                modifier = Modifier.align(Alignment.TopEnd).padding(4.dp),
            )
        }
        if (onLongPress == null) DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
            DropdownMenuItem(
                text = { Text(stringResource(R.string.media_add_favorite)) },
                leadingIcon = { Icon(Icons.Outlined.FavoriteBorder, contentDescription = null) },
                onClick = { menuOpen = false; onFavorite() },
            )
            DropdownMenuItem(
                text = { Text(stringResource(R.string.vault_add)) },
                leadingIcon = { Icon(Icons.Outlined.VisibilityOff, contentDescription = null) },
                onClick = { menuOpen = false; onAddToVault() },
            )
        }
    }
}
