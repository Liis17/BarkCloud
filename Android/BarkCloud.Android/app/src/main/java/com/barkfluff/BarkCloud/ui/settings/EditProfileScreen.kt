package com.barkfluff.BarkCloud.ui.settings

import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.content.FileProvider
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.platform.LocalContext
import com.barkfluff.BarkCloud.BuildConfig
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.barkfluff.BarkCloud.R

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun EditProfileScreen(
    onNavigateUp: () -> Unit,
    onSignedOut: () -> Unit,
    viewModel: EditProfileViewModel = viewModel(factory = EditProfileViewModel.factory()),
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val snackbarHostState = remember { SnackbarHostState() }
    val context = LocalContext.current
    var pendingAvatar by rememberSaveable { mutableStateOf<String?>(null) }
    val avatarPicker = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        pendingAvatar = uri?.toString()
    }

    LaunchedEffect(Unit) { viewModel.load() }
    LaunchedEffect(viewModel) {
        viewModel.saved.collect { onNavigateUp() }
    }
    LaunchedEffect(viewModel) {
        viewModel.signedOut.collect { onSignedOut() }
    }
    LaunchedEffect(state.snackbar) {
        val msg = state.snackbar ?: return@LaunchedEffect
        snackbarHostState.showSnackbar(msg)
        viewModel.snackbarShown()
    }

    pendingAvatar?.let { pending ->
        AvatarCropDialog(
            uri = Uri.parse(pending),
            onDismiss = { pendingAvatar = null },
            onConfirm = { file ->
                val croppedUri = FileProvider.getUriForFile(context, "${BuildConfig.APPLICATION_ID}.fileprovider", file)
                viewModel.setAvatar(croppedUri) { file.delete() }
                pendingAvatar = null
            },
        )
    }

    Scaffold(
        contentWindowInsets = androidx.compose.foundation.layout.WindowInsets(0, 0, 0, 0),
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.edit_profile_title)) },
                windowInsets = androidx.compose.foundation.layout.WindowInsets(0, 0, 0, 0),
                navigationIcon = {
                    IconButton(onClick = onNavigateUp) {
                        Icon(Icons.AutoMirrored.Outlined.ArrowBack, contentDescription = null)
                    }
                },
                actions = {
                    TextButton(onClick = viewModel::save, enabled = state.canSave) {
                        if (state.isSaving) {
                            CircularProgressIndicator(strokeWidth = 2.dp, modifier = Modifier.padding(4.dp))
                        } else {
                            Text(stringResource(R.string.common_save))
                        }
                    }
                },
            )
        },
        snackbarHost = { SnackbarHost(snackbarHostState) },
    ) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .verticalScroll(rememberScrollState())
                .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            ProfileHeader(
                state = state,
                onPickAvatar = {
                    avatarPicker.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly))
                },
                onRemoveAvatar = viewModel::removeAvatar,
            )
            OutlinedTextField(
                value = state.firstName,
                onValueChange = viewModel::onFirstNameChange,
                label = { Text(stringResource(R.string.edit_profile_first_name)) },
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )
            OutlinedTextField(
                value = state.lastName,
                onValueChange = viewModel::onLastNameChange,
                label = { Text(stringResource(R.string.edit_profile_last_name)) },
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )
            OutlinedTextField(
                value = state.username,
                onValueChange = viewModel::onUsernameChange,
                label = { Text(stringResource(R.string.edit_profile_username)) },
                singleLine = true,
                isError = state.usernameTaken,
                supportingText = if (state.usernameTaken) {
                    { Text(stringResource(R.string.edit_profile_username_taken)) }
                } else null,
                modifier = Modifier.fillMaxWidth(),
            )
            OutlinedTextField(
                value = state.bio,
                onValueChange = viewModel::onBioChange,
                label = { Text(stringResource(R.string.edit_profile_bio)) },
                isError = state.bioTooLong,
                supportingText = {
                    Text(
                        text = stringResource(R.string.edit_profile_bio_counter, state.bio.length),
                        color = if (state.bioTooLong) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                },
                minLines = 3,
                modifier = Modifier.fillMaxWidth(),
            )
            ProfileAccountActions(
                enabled = !state.isSaving && !state.isUpdatingAvatar && !state.isProcessing,
                isProcessing = state.isProcessing,
                onSignOut = viewModel::signOut,
                onDeleteAccount = viewModel::deleteAccount,
            )
        }
    }
}
