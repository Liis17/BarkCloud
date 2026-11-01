package com.barkfluff.BarkCloud.net

import com.barkfluff.BarkCloud.grpc.ServerConfig
import com.barkfluff.BarkCloud.grpc.ServerSettings
import okhttp3.Call
import okhttp3.OkHttpClient
import okhttp3.Request
import java.util.concurrent.TimeUnit

/** One client generation per server policy, shared by Coil, downloads and workers. */
class ServerHttpClients(private val config: () -> ServerConfig) : Call.Factory {
    private data class Clients(
        val key: String,
        val strict: OkHttpClient,
        val selfSigned: OkHttpClient,
        val streamingStrict: OkHttpClient,
        val streamingSelfSigned: OkHttpClient,
    )
    private var clients: Clients? = null

    /**
     * Клиент для потоковых ответов (видео). Те же правила TLS и хоста, что у обычного клиента,
     * но без whole-call timeout: длинный поток не должен обрываться через 600 секунд.
     */
    val streaming: Call.Factory = object : Call.Factory {
        override fun newCall(request: Request): Call = clientFor(request, streaming = true).newCall(request)
    }

    @Synchronized
    fun invalidate() {
        clients?.strict?.dispatcher?.cancelAll()
        clients?.strict?.connectionPool?.evictAll()
        clients = null
    }

    @Synchronized
    override fun newCall(request: Request): Call = clientFor(request, streaming = false).newCall(request)

    @Synchronized
    internal fun clientFor(request: Request, streaming: Boolean): OkHttpClient {
        val selected = config()
        if (clients?.key != selected.key) {
            invalidate()
            val strict = OkHttpClient.Builder()
                .connectTimeout(60, TimeUnit.SECONDS).readTimeout(120, TimeUnit.SECONDS)
                .writeTimeout(600, TimeUnit.SECONDS).callTimeout(600, TimeUnit.SECONDS).build()
            val selfSigned = strict.newBuilder()
                .sslSocketFactory(InsecureTls.socketFactory(), InsecureTls.trustManager)
                // Redirects cannot carry this host's certificate exception to another host.
                .followRedirects(false).followSslRedirects(false).build()
            clients = Clients(
                key = selected.key,
                strict = strict,
                selfSigned = selfSigned,
                streamingStrict = strict.newBuilder().callTimeout(0, TimeUnit.MILLISECONDS).build(),
                streamingSelfSigned = selfSigned.newBuilder().callTimeout(0, TimeUnit.MILLISECONDS).build(),
            )
        }
        val current = checkNotNull(clients)
        val permitted = selected.usesTls && selected.allowSelfSigned && request.url.isHttps &&
            request.url.host.equals(selected.hostname, ignoreCase = true)
        return when {
            permitted && streaming -> current.streamingSelfSigned
            permitted -> current.selfSigned
            streaming -> current.streamingStrict
            else -> current.strict
        }
    }
}

object InsecureHttp {
    private val clients = ServerHttpClients { ServerSettings.config }
    val client: Call.Factory = clients
    val streaming: Call.Factory = clients.streaming
    fun reset() = clients.invalidate()
}
