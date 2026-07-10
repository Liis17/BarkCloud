package com.barkfluff.BarkCloud.ui.settings

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material3.AssistChip
import androidx.compose.material3.ElevatedCard
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.gallery.AutoUploadNetworkPolicy
import com.barkfluff.BarkCloud.data.gallery.AutoUploadScheduler
import com.barkfluff.BarkCloud.data.gallery.AutoUploadSettings
import kotlinx.coroutines.launch

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun UploadSettingsScreen(onNavigateUp: () -> Unit) {
    val context = LocalContext.current
    val app = context.applicationContext as BarkCloudApplication
    val settings = remember { AutoUploadSettings(context) }
    val scope = rememberCoroutineScope()
    var selected by remember { mutableStateOf(settings.policy) }

    fun select(policy: AutoUploadNetworkPolicy) {
        selected = policy
        settings.policy = policy
        scope.launch {
            if (policy == AutoUploadNetworkPolicy.OFF) {
                AutoUploadScheduler.disable(context)
                app.uploadQueue.pauseBackup()
            } else {
                app.uploadQueue.resumeBackup()
                AutoUploadScheduler.apply(context, policy)
            }
        }
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Загрузки") },
                navigationIcon = {
                    IconButton(onClick = onNavigateUp) {
                        Icon(Icons.AutoMirrored.Outlined.ArrowBack, contentDescription = null)
                    }
                },
            )
        },
    ) { padding ->
        Column(
            modifier = Modifier.fillMaxSize().padding(padding).padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            ElevatedCard(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    Text("Автозагрузка медиатеки")
                    Text("Ручная отправка всегда доступна при наличии сети.")
                    PolicyChip("Только Wi‑Fi", AutoUploadNetworkPolicy.WIFI_ONLY, selected, ::select)
                    PolicyChip("Wi‑Fi и мобильная сеть", AutoUploadNetworkPolicy.ANY_NETWORK, selected, ::select)
                    PolicyChip("Отключено", AutoUploadNetworkPolicy.OFF, selected, ::select)
                }
            }
        }
    }
}

@Composable
private fun PolicyChip(
    title: String,
    policy: AutoUploadNetworkPolicy,
    selected: AutoUploadNetworkPolicy,
    onSelect: (AutoUploadNetworkPolicy) -> Unit,
) {
    AssistChip(onClick = { onSelect(policy) }, enabled = policy != selected, label = { Text(title) })
}
