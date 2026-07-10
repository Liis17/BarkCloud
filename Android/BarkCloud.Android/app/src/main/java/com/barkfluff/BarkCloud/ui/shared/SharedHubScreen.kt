package com.barkfluff.BarkCloud.ui.shared

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.CenterAlignedTopAppBar
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import com.barkfluff.BarkCloud.R
import kotlinx.coroutines.launch

/** Контейнер таба «Общие файлы»: 3 сегмента — публичные ссылки, исходящие и входящие гранты. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SharedHubScreen(
    onOpenSharedFolder: (directoryId: String, name: String) -> Unit,
) {
    var segment by rememberSaveable { mutableIntStateOf(0) }
    val snackbarHostState = remember { SnackbarHostState() }
    val scope = rememberCoroutineScope()
    val onSnackbar: (String) -> Unit = { msg -> scope.launch { snackbarHostState.showSnackbar(msg) } }

    val segments = listOf(
        stringResource(R.string.shared_tab_my_public),
        stringResource(R.string.shared_tab_i_shared),
        stringResource(R.string.shared_tab_shared_with_me),
    )

    Scaffold(
        topBar = { CenterAlignedTopAppBar(title = { Text(stringResource(R.string.files_shared_title)) }) },
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
                0 -> MySharesScreen(onSnackbar = onSnackbar)
                1 -> MyOutgoingSharesScreen(onSnackbar = onSnackbar)
                else -> SharedWithMeScreen(onOpenFolder = onOpenSharedFolder, onSnackbar = onSnackbar)
            }
        }
    }
}
