package com.barkfluff.BarkCloud.ui.server

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.barkfluff.BarkCloud.grpc.ServerConfig
import com.barkfluff.BarkCloud.grpc.ServerConnectionProbe
import com.barkfluff.BarkCloud.grpc.validateServerConfig
import kotlinx.coroutines.Job
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

private val serviceNames = listOf("barkcloud.identity.IdentityApi", "barkcloud.users.UsersApi", "barkcloud.files.FilesApi")
enum class ServiceStatus { IDLE, CHECKING, SUCCESS, FAILED }
data class ServerUiState(
    val host: String,
    val identityPort: String,
    val usersPort: String,
    val filesPort: String,
    val allowSelfSigned: Boolean,
    val isLoading: Boolean = false,
    val status: List<ServiceStatus> = List(3) { ServiceStatus.IDLE },
    val error: String? = null,
)

class ServerViewModel(
    initial: ServerConfig,
    private val defaults: ServerConfig,
    private val probe: ServerConnectionProbe,
    private val save: (ServerConfig) -> Unit,
) : ViewModel() {
    private val mutable = MutableStateFlow(initial.toUiState())
    val state = mutable.asStateFlow()
    private val completed = Channel<Unit>(Channel.BUFFERED)
    val events = completed.receiveAsFlow()
    private var job: Job? = null
    private var revision = 0

    fun hostChanged(value: String) = edit { it.copy(host = value, allowSelfSigned = false) }
    fun portChanged(index: Int, value: String) = edit {
        val digits = value.filter { it in '0'..'9' }.take(5)
        when (index) {
            0 -> it.copy(identityPort = digits)
            1 -> it.copy(usersPort = digits)
            else -> it.copy(filesPort = digits)
        }
    }
    fun trustChanged(value: Boolean) = edit { it.copy(allowSelfSigned = value) }
    fun defaultPorts() = edit { it.copy(identityPort = defaults.identityPort.toString(), usersPort = defaults.usersPort.toString(), filesPort = defaults.filesPort.toString()) }

    private fun edit(transform: (ServerUiState) -> ServerUiState) {
        revision++
        job?.cancel()
        mutable.update { transform(it).copy(isLoading = false, error = null, status = List(3) { ServiceStatus.IDLE }) }
    }

    fun connect() {
        val current = state.value
        if (current.isLoading) return
        val validated = validateServerConfig(current.host, current.identityPort, current.usersPort, current.filesPort, current.allowSelfSigned)
        val config = validated.config ?: run { mutable.update { it.copy(error = validated.error) }; return }
        val activeRevision = revision
        mutable.update { it.copy(isLoading = true, error = null, status = List(3) { ServiceStatus.CHECKING }) }
        job = viewModelScope.launch {
            val ports = listOf(config.identityPort, config.usersPort, config.filesPort)
            val result = ports.mapIndexed { index, port -> async {
                val success = probe.check(config, port, serviceNames[index]).isSuccess
                if (revision == activeRevision) mutable.update {
                    it.copy(status = it.status.toMutableList().also { statuses -> statuses[index] = if (success) ServiceStatus.SUCCESS else ServiceStatus.FAILED })
                }
                success
            } }.awaitAll()
            if (revision != activeRevision) return@launch
            if (result.all { it }) {
                save(config)
                mutable.update { it.copy(isLoading = false) }
                completed.send(Unit)
            } else mutable.update { it.copy(isLoading = false, error = "Не удалось подключиться ко всем сервисам. Проверьте адрес, порты и сертификат.") }
        }
    }
}

private fun ServerConfig.toUiState() = ServerUiState(host, identityPort.toString(), usersPort.toString(), filesPort.toString(), allowSelfSigned)
