package com.barkfluff.BarkCloud.grpc

import android.content.Context
import android.content.SharedPreferences
import com.barkfluff.BarkCloud.BuildConfig

/**
 * Рантайм-адреса сервера. По умолчанию собираются из зашитых при сборке адресов
 * (`BuildConfig.*`), но переопределяются на экране входа — поля адреса сервера и
 * портов. Object, а не DI-класс: адреса читаются из мест без зависимостей
 * ([GrpcEndpoint], `SharedModels`, gRPC-стабы [GrpcManager], workers).
 */
object ServerSettings {

    private const val PREFS_NAME = "barkcloud_server"
    private const val KEY_HOST = "host"
    private const val KEY_IDENTITY_PORT = "identity_port"
    private const val KEY_USERS_PORT = "users_port"
    private const val KEY_FILES_PORT = "files_port"

    private var prefs: SharedPreferences? = null

    /** Вызвать один раз из `Application.onCreate` до первого обращения к адресам. */
    fun init(context: Context) {
        if (prefs == null) {
            prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
        }
    }

    /** Хост сервера: `host`, `http://host` или `https://host` (без scheme — https). */
    val host: String
        get() = prefs()?.getString(KEY_HOST, null)?.takeIf { it.isNotBlank() } ?: defaultHost

    val identityPort: Int
        get() = port(KEY_IDENTITY_PORT, defaultIdentityPort)

    val usersPort: Int
        get() = port(KEY_USERS_PORT, defaultUsersPort)

    val filesPort: Int
        get() = port(KEY_FILES_PORT, defaultFilesPort)

    val identityAddress: String get() = address(identityPort)
    val usersAddress: String get() = address(usersPort)
    val filesAddress: String get() = address(filesPort)

    /** База HTTP-раздачи файлов (`/web/download/{id}`, `/web/upload/{id}`). */
    val filesWebBase: String get() = "${address(filesPort)}/web"

    /** База data plane Upload 2.0 (`/file-upload/{session}/parts/{n}`) — порт nginx. */
    val fileUploadBase: String get() = address(null)

    /** Хост без порта: публичные ссылки `/s|f|al/{token}` рендерит веб-клиент на 443. */
    val webHostBase: String get() = address(null)

    /**
     * Сохраняет значения из формы настроек сервера. Пустой/невалидный порт —
     * возврат к дефолту сборки.
     */
    fun save(host: String, identityPort: String, usersPort: String, filesPort: String) {
        val trimmed = host.trim()
        if (trimmed.isBlank()) edit { it.remove(KEY_HOST) } else edit { it.putString(KEY_HOST, trimmed) }
        putPort(KEY_IDENTITY_PORT, identityPort)
        putPort(KEY_USERS_PORT, usersPort)
        putPort(KEY_FILES_PORT, filesPort)
    }

    private fun address(port: Int?): String {
        val raw = host
        val scheme = if (raw.startsWith("http://")) "http" else "https"
        val hostOnly = raw.removePrefix("https://").removePrefix("http://").trimEnd('/')
        return if (port != null) "$scheme://$hostOnly:$port" else "$scheme://$hostOnly"
    }

    private fun port(key: String, default: Int): Int =
        prefs()?.getInt(key, 0)?.takeIf { it > 0 } ?: default

    private fun putPort(key: String, value: String) {
        val port = value.trim().toIntOrNull()?.takeIf { it in 1..65535 }
        if (port != null) edit { it.putInt(key, port) } else edit { it.remove(key) }
    }

    private fun prefs(): SharedPreferences? = prefs

    private fun edit(block: (SharedPreferences.Editor) -> Unit) {
        prefs()?.edit()?.also(block)?.apply()
    }

    private val defaultHost: String = BuildConfig.IDENTITY_API_ADDRESS
        .substringAfter("://")
        .substringBefore(":")

    private val defaultIdentityPort: Int = defaultPort(BuildConfig.IDENTITY_API_ADDRESS)
    private val defaultUsersPort: Int = defaultPort(BuildConfig.USERS_API_ADDRESS)
    private val defaultFilesPort: Int = defaultPort(BuildConfig.FILES_API_ADDRESS)

    private fun defaultPort(address: String): Int =
        address.substringAfterLast(":").toIntOrNull() ?: 443
}
