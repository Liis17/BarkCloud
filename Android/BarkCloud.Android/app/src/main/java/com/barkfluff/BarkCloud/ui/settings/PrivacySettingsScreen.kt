package com.barkfluff.BarkCloud.ui.settings

import barkcloud.users.UsersApiOuterClass.PrivacyVisibility
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.ArrowDropDown
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.barkfluff.BarkCloud.R

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PrivacySettingsScreen(
    onNavigateUp: () -> Unit,
    viewModel: PrivacyViewModel = viewModel(factory = PrivacyViewModel.factory()),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val snackbarHostState = remember { SnackbarHostState() }

    LaunchedEffect(Unit) { viewModel.load() }
    LaunchedEffect(state.snackbar) {
        val msg = state.snackbar ?: return@LaunchedEffect
        snackbarHostState.showSnackbar(msg)
        viewModel.snackbarShown()
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.privacy_title)) },
                navigationIcon = {
                    IconButton(onClick = onNavigateUp) {
                        Icon(Icons.AutoMirrored.Outlined.ArrowBack, contentDescription = null)
                    }
                },
            )
        },
        snackbarHost = { SnackbarHost(snackbarHostState) },
    ) { padding ->
        val settings = state.settings
        Column(Modifier.fillMaxSize().padding(padding)) {
            if (settings != null) {
                VisibilityRow(
                    label = stringResource(R.string.privacy_profile),
                    value = settings.profileVisibility,
                    onChange = viewModel::setProfileVisibility,
                )
                HorizontalDivider()
                VisibilityRow(
                    label = stringResource(R.string.privacy_email),
                    value = settings.emailVisibility,
                    onChange = viewModel::setEmailVisibility,
                )
                HorizontalDivider()
                VisibilityRow(
                    label = stringResource(R.string.privacy_last_seen),
                    value = settings.lastSeenVisibility,
                    onChange = viewModel::setLastSeenVisibility,
                )
                HorizontalDivider()
                ListItem(
                    headlineContent = { Text(stringResource(R.string.privacy_searchable)) },
                    trailingContent = {
                        Switch(
                            checked = settings.searchableByUsername,
                            onCheckedChange = viewModel::setSearchable,
                        )
                    },
                )
            }
        }
    }
}

@Composable
private fun VisibilityRow(
    label: String,
    value: PrivacyVisibility,
    onChange: (PrivacyVisibility) -> Unit,
) {
    var expanded by remember { mutableStateOf(false) }
    ListItem(
        headlineContent = { Text(label) },
        trailingContent = {
            Box {
                TextButton(onClick = { expanded = true }) {
                    Text(visibilityLabel(value))
                    Icon(Icons.Outlined.ArrowDropDown, contentDescription = null)
                }
                DropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
                    visibilityOptions.forEach { option ->
                        DropdownMenuItem(
                            text = { Text(visibilityLabel(option)) },
                            onClick = { expanded = false; onChange(option) },
                        )
                    }
                }
            }
        },
    )
}

private val visibilityOptions = listOf(
    PrivacyVisibility.PRIVACY_VISIBILITY_EVERYONE,
    PrivacyVisibility.PRIVACY_VISIBILITY_CONTACTS,
    PrivacyVisibility.PRIVACY_VISIBILITY_NOBODY,
)

@Composable
private fun visibilityLabel(v: PrivacyVisibility): String = stringResource(
    when (v) {
        PrivacyVisibility.PRIVACY_VISIBILITY_CONTACTS -> R.string.privacy_contacts
        PrivacyVisibility.PRIVACY_VISIBILITY_NOBODY -> R.string.privacy_nobody
        else -> R.string.privacy_everyone
    }
)
