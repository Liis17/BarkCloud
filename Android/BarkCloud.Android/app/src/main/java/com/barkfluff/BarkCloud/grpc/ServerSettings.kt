package com.barkfluff.BarkCloud.grpc

import android.content.Context
import android.content.SharedPreferences
import com.barkfluff.BarkCloud.BuildConfig
import java.net.URI

/** One selected server; its TLS policy is part of the configuration identity. */
data class ServerConfig(
    val host: String,
    val identityPort: Int = 8000,
    val usersPort: Int = 8001,
    val filesPort: Int = 8005,
    val allowSelfSigned: Boolean = false,
) {
    val uri: URI get() = URI(host)
    val hostname: String get() = uri.host.removeSurrounding("[", "]")
    val usesTls: Boolean get() = uri.scheme == "https"
    fun address(port: Int? = null): String = URI(
        uri.scheme, null, hostname, port ?: -1, null, null, null,
    ).toASCIIString()
    val key: String get() = "$host|$identityPort|$usersPort|$filesPort|$allowSelfSigned"
}

data class ServerValidation(val config: ServerConfig? = null, val error: String? = null)

fun validateServerConfig(
    host: String,
    identityPort: String,
    usersPort: String,
    filesPort: String,
    allowSelfSigned: Boolean,
): ServerValidation {
    val raw = host.trim()
    if (raw.isEmpty()) return ServerValidation(error = "Введите адрес сервера")
    val uri = runCatching { URI(if ("://" in raw) raw else "https://$raw") }.getOrNull()
    if (uri == null || uri.scheme !in setOf("http", "https") || uri.host.isNullOrBlank() ||
        uri.userInfo != null || uri.port != -1 || uri.rawQuery != null || uri.rawFragment != null ||
        (!uri.path.isNullOrEmpty() && uri.path != "/")
    ) return ServerValidation(error = "Укажите домен или IP без порта и пути. Порты задаются ниже.")
    val ports = listOf(identityPort, usersPort, filesPort).map { it.trim().toIntOrNull() }
    if (ports.any { it == null || it !in 1..65535 }) {
        return ServerValidation(error = "Каждый порт должен быть числом от 1 до 65535")
    }
    val normalized = URI(uri.scheme, null, uri.host.lowercase().removeSurrounding("[", "]"), -1, null, null, null).toASCIIString()
    return ServerValidation(ServerConfig(normalized, ports[0]!!, ports[1]!!, ports[2]!!, allowSelfSigned && uri.scheme == "https"))
}

object ServerSettings {
    private const val PREFS_NAME = "barkcloud_server"
    private var prefs: SharedPreferences? = null

    fun init(context: Context) {
        if (prefs != null) return
        val saved = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
        if (!saved.contains("allow_self_signed")) {
            val legacy = saved.all.isNotEmpty() ||
                context.getSharedPreferences("barkcloud_token_store", Context.MODE_PRIVATE).contains("payload") ||
                context.getSharedPreferences("barkcloud_secure_prefs", Context.MODE_PRIVATE).all.isNotEmpty()
            saved.edit().putBoolean("allow_self_signed", legacy).apply()
        }
        prefs = saved
    }

    val hasConfiguredServer: Boolean get() = prefs?.let {
        it.getBoolean("configured", false) || it.contains("host") || it.contains("identity_port")
    } ?: false
    val defaults: ServerConfig get() = ServerConfig(
        host = "https://${URI(BuildConfig.IDENTITY_API_ADDRESS).host}",
        identityPort = URI(BuildConfig.IDENTITY_API_ADDRESS).port.takeIf { it > 0 } ?: 443,
        usersPort = URI(BuildConfig.USERS_API_ADDRESS).port.takeIf { it > 0 } ?: 443,
        filesPort = URI(BuildConfig.FILES_API_ADDRESS).port.takeIf { it > 0 } ?: 443,
    )
    val config: ServerConfig get() {
        val d = defaults
        val raw = prefs?.getString("host", null)?.takeIf { it.isNotBlank() } ?: d.host
        return validateServerConfig(
            raw,
            (prefs?.getInt("identity_port", d.identityPort) ?: d.identityPort).toString(),
            (prefs?.getInt("users_port", d.usersPort) ?: d.usersPort).toString(),
            (prefs?.getInt("files_port", d.filesPort) ?: d.filesPort).toString(),
            prefs?.getBoolean("allow_self_signed", false) ?: false,
        ).config ?: d
    }
    val host: String get() = config.host
    val identityPort: Int get() = config.identityPort
    val usersPort: Int get() = config.usersPort
    val filesPort: Int get() = config.filesPort
    val identityAddress: String get() = config.address(config.identityPort)
    val usersAddress: String get() = config.address(config.usersPort)
    val filesAddress: String get() = config.address(config.filesPort)
    val filesWebBase: String get() = "$filesAddress/web"
    val fileUploadBase: String get() = config.address()
    val webHostBase: String get() = config.address()

    fun save(config: ServerConfig) {
        checkNotNull(prefs).edit()
            .putString("host", config.host)
            .putInt("identity_port", config.identityPort)
            .putInt("users_port", config.usersPort)
            .putInt("files_port", config.filesPort)
            .putBoolean("allow_self_signed", config.allowSelfSigned)
            .putBoolean("configured", true)
            .apply()
    }
}
