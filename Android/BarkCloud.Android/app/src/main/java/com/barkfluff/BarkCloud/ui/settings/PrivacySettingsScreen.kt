package com.barkfluff.BarkCloud.ui.settings

import barkcloud.users.UsersApiOuterClass.PrivacyVisibility
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.Check
import androidx.compose.material.icons.outlined.Group
import androidx.compose.material.icons.outlined.Lock
import androidx.compose.material.icons.outlined.Public
import androidx.compose.material.icons.outlined.Search
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ElevatedCard
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.ExposedDropdownMenuAnchorType
import androidx.compose.material3.ExposedDropdownMenuBox
import androidx.compose.material3.ExposedDropdownMenuDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearWavyProgressIndicator
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.barkfluff.BarkCloud.R

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
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
        Column(
            Modifier
                .fillMaxSize()
                .padding(padding)
                .verticalScroll(rememberScrollState())
                .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            if (settings != null) {
                PrivacySection(stringResource(R.string.privacy_section_visibility)) {
                    ElevatedCard(Modifier.fillMaxWidth()) {
                        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
                            VisibilityDropdown(
                                label = stringResource(R.string.privacy_profile),
                                value = settings.profileVisibility,
                                onChange = viewModel::setProfileVisibility,
                            )
                            VisibilityDropdown(
                                label = stringResource(R.string.privacy_email),
                                value = settings.emailVisibility,
                                onChange = viewModel::setEmailVisibility,
                            )
                            VisibilityDropdown(
                                label = stringResource(R.string.privacy_last_seen),
                                value = settings.lastSeenVisibility,
                                onChange = viewModel::setLastSeenVisibility,
                            )
                        }
                    }
                }

                PrivacySection(stringResource(R.string.privacy_section_search)) {
                    ElevatedCard(Modifier.fillMaxWidth()) {
                        ListItem(
                            headlineContent = { Text(stringResource(R.string.privacy_searchable)) },
                            supportingContent = { Text(stringResource(R.string.privacy_searchable_description)) },
                            leadingContent = { Icon(Icons.Outlined.Search, contentDescription = null) },
                            trailingContent = {
                                Switch(
                                    checked = settings.searchableByUsername,
                                    onCheckedChange = viewModel::setSearchable,
                                )
                            },
                        )
                    }
                }
            } else if (state.isLoading) {
                LinearWavyProgressIndicator(Modifier.fillMaxWidth())
            }
        }
    }
}

@Composable
private fun PrivacySection(title: String, content: @Composable () -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text(
            title,
            style = MaterialTheme.typography.titleSmall,
            color = MaterialTheme.colorScheme.primary,
            modifier = Modifier.padding(horizontal = 4.dp),
        )
        content()
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun VisibilityDropdown(
    label: String,
    value: PrivacyVisibility,
    onChange: (PrivacyVisibility) -> Unit,
) {
    var expanded by remember { mutableStateOf(false) }
    ExposedDropdownMenuBox(expanded = expanded, onExpandedChange = { expanded = it }) {
        OutlinedTextField(
            value = visibilityLabel(value),
            onValueChange = {},
            readOnly = true,
            label = { Text(label) },
            leadingIcon = { Icon(visibilityIcon(value), contentDescription = null) },
            trailingIcon = { ExposedDropdownMenuDefaults.TrailingIcon(expanded = expanded) },
            modifier = Modifier
                .fillMaxWidth()
                .menuAnchor(ExposedDropdownMenuAnchorType.PrimaryNotEditable),
        )
        ExposedDropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
            visibilityOptions.forEach { option ->
                DropdownMenuItem(
                    text = {
                        Column {
                            Text(visibilityLabel(option), style = MaterialTheme.typography.bodyLarge)
                            Text(
                                visibilityDescription(option),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    },
                    leadingIcon = { Icon(visibilityIcon(option), contentDescription = null) },
                    trailingIcon = {
                        if (option == value) Icon(Icons.Outlined.Check, contentDescription = null)
                    },
                    onClick = {
                        expanded = false
                        onChange(option)
                    },
                )
            }
        }
    }
}

private val visibilityOptions = listOf(
    PrivacyVisibility.PRIVACY_VISIBILITY_EVERYONE,
    PrivacyVisibility.PRIVACY_VISIBILITY_CONTACTS,
    PrivacyVisibility.PRIVACY_VISIBILITY_NOBODY,
)

private fun visibilityIcon(v: PrivacyVisibility): ImageVector = when (v) {
    PrivacyVisibility.PRIVACY_VISIBILITY_CONTACTS -> Icons.Outlined.Group
    PrivacyVisibility.PRIVACY_VISIBILITY_NOBODY -> Icons.Outlined.Lock
    else -> Icons.Outlined.Public
}

@Composable
private fun visibilityLabel(v: PrivacyVisibility): String = stringResource(
    when (v) {
        PrivacyVisibility.PRIVACY_VISIBILITY_CONTACTS -> R.string.privacy_contacts
        PrivacyVisibility.PRIVACY_VISIBILITY_NOBODY -> R.string.privacy_nobody
        else -> R.string.privacy_everyone
    }
)

@Composable
private fun visibilityDescription(v: PrivacyVisibility): String = stringResource(
    when (v) {
        PrivacyVisibility.PRIVACY_VISIBILITY_CONTACTS -> R.string.privacy_desc_contacts
        PrivacyVisibility.PRIVACY_VISIBILITY_NOBODY -> R.string.privacy_desc_nobody
        else -> R.string.privacy_desc_everyone
    }
)
