package com.barkfluff.BarkCloud.ui.settings

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.PhotoCamera
import androidx.compose.material.icons.outlined.Person
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.ui.components.RemoteImage

@Composable
internal fun ProfileHeader(
    state: EditProfileUiState,
    onPickAvatar: () -> Unit,
    onRemoveAvatar: () -> Unit,
) {
    Column(
        modifier = Modifier.fillMaxWidth(),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Box(
            modifier = Modifier
                .size(96.dp)
                .clickable(enabled = !state.isLoading && !state.isUpdatingAvatar && !state.isSaving, onClick = onPickAvatar),
            contentAlignment = Alignment.Center,
        ) {
            Box(
                Modifier.fillMaxSize().clip(CircleShape).background(MaterialTheme.colorScheme.surfaceVariant),
                contentAlignment = Alignment.Center,
            ) {
                if (state.avatarUrl != null) {
                    RemoteImage(
                        url = state.avatarUrl,
                        contentDescription = null,
                        normalize = true,
                        modifier = Modifier.fillMaxSize(),
                    )
                } else {
                    Icon(
                        Icons.Outlined.Person,
                        contentDescription = null,
                        modifier = Modifier.size(48.dp),
                        tint = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
            if (state.isUpdatingAvatar) {
                Box(
                    Modifier.fillMaxSize().clip(CircleShape).background(MaterialTheme.colorScheme.scrim.copy(alpha = 0.4f)),
                    contentAlignment = Alignment.Center,
                ) { CircularProgressIndicator(strokeWidth = 2.dp, modifier = Modifier.size(28.dp)) }
            } else {
                Box(
                    Modifier.fillMaxSize(),
                    contentAlignment = Alignment.BottomEnd,
                ) {
                    Surface(color = MaterialTheme.colorScheme.primary, shape = CircleShape) {
                        Icon(
                            Icons.Filled.PhotoCamera,
                            contentDescription = stringResource(R.string.settings_avatar_change),
                            tint = MaterialTheme.colorScheme.onPrimary,
                            modifier = Modifier.padding(4.dp).size(16.dp),
                        )
                    }
                }
            }
        }

        Text(state.displayName, style = MaterialTheme.typography.headlineSmall)
        if (state.username.isNotEmpty()) {
            Text("@${state.username}", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        if (state.bio.isNotEmpty()) {
            Text(state.bio, style = MaterialTheme.typography.bodyMedium, textAlign = TextAlign.Center)
        }
        if (state.hasAvatar) {
            TextButton(onClick = onRemoveAvatar, enabled = !state.isUpdatingAvatar && !state.isSaving && !state.isLoading) {
                Text(stringResource(R.string.settings_avatar_remove))
            }
        }
    }
}
