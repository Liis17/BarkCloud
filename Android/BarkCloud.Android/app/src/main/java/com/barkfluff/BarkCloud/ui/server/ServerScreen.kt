package com.barkfluff.BarkCloud.ui.server

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.selection.toggleable
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.CheckCircle
import androidx.compose.material.icons.outlined.ErrorOutline
import androidx.compose.material.icons.outlined.Dns
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.barkfluff.BarkCloud.ui.auth.components.*

@Composable
fun ServerScreen(viewModel: ServerViewModel, onConnected: () -> Unit, onBack: (() -> Unit)? = null) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    LaunchedEffect(viewModel) { viewModel.events.collect { onConnected() } }
    AuthScaffold("Ваш сервер", onBack, kind = AuthDecorationKind.SERVER, actions = {
        AuthError(state.error)
        AuthPrimaryButton("Подключиться", viewModel::connect, loading = state.isLoading)
    }) {
        AuthTextField(state.host, viewModel::hostChanged, "Адрес сервера", keyboardType = KeyboardType.Uri)
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
            Text("Порты", style = MaterialTheme.typography.titleMedium)
            TextButton(viewModel::defaultPorts, Modifier.heightIn(min = 48.dp), enabled = !state.isLoading) { Text("По умолчанию") }
        }
        val largeText = androidx.compose.ui.platform.LocalDensity.current.fontScale > 1.3f
        @Composable fun Port(index: Int, modifier: Modifier = Modifier) {
            Column(modifier, verticalArrangement = Arrangement.spacedBy(6.dp)) {
                Text(listOf("Identity", "Users", "Files")[index], style = MaterialTheme.typography.labelMedium)
                AuthTextField(listOf(state.identityPort, state.usersPort, state.filesPort)[index],
                    { viewModel.portChanged(index, it) }, "Порт", keyboardType = KeyboardType.Number)
            }
        }
        if (largeText) repeat(3) { Port(it) }
        else Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) { repeat(3) { Port(it, Modifier.weight(1f)) } }
        Row(Modifier.fillMaxWidth().toggleable(state.allowSelfSigned, role = Role.Switch, onValueChange = viewModel::trustChanged)
            .padding(vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f).padding(end = 12.dp)) {
                Text("Самоподписанный сертификат", style = MaterialTheme.typography.titleSmall)
                Text("Разрешить для этого сервера. Имя в сертификате должно совпадать с адресом.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            Switch(state.allowSelfSigned, onCheckedChange = null)
        }
        Surface(color = MaterialTheme.colorScheme.surfaceContainer, shape = androidx.compose.foundation.shape.RoundedCornerShape(20.dp)) {
            Column(Modifier.fillMaxWidth().padding(16.dp).semantics { liveRegion = LiveRegionMode.Polite }, verticalArrangement = Arrangement.spacedBy(12.dp)) {
                listOf("Identity", "Users", "Files").forEachIndexed { index, label ->
                    val status = state.status[index]
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        if (status == ServiceStatus.CHECKING) CircularProgressIndicator(Modifier.size(24.dp), strokeWidth = 2.dp)
                        else Icon(when (status) { ServiceStatus.SUCCESS -> Icons.Outlined.CheckCircle; ServiceStatus.FAILED -> Icons.Outlined.ErrorOutline; else -> Icons.Outlined.Dns }, null,
                            tint = if (status == ServiceStatus.FAILED) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.primary)
                        Text(label, Modifier.weight(1f))
                        Text(when (status) { ServiceStatus.IDLE -> "Не проверен"; ServiceStatus.CHECKING -> "Проверяем"; ServiceStatus.SUCCESS -> "Доступен"; ServiceStatus.FAILED -> "Недоступен" }, style = MaterialTheme.typography.bodySmall)
                    }
                }
            }
        }
    }
}
