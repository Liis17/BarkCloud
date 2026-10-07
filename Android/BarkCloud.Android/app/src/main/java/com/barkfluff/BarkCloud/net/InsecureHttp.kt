package com.barkfluff.BarkCloud.net

import com.barkfluff.BarkCloud.grpc.ServerConfig
import com.barkfluff.BarkCloud.grpc.ServerSettings
import okhttp3.Call
import okhttp3.OkHttpClient
import okhttp3.Request
import java.util.concurrent.TimeUnit

/** One client generation per server policy, shared by Coil, downloads and workers. */
class ServerHttpClients(private val config: () -> ServerConfig) : Call.Factory {
    private data class Clients(val key: String, val strict: OkHttpClient, val selfSigned: OkHttpClient)
    private var clients: Clients? = null

    @Synchronized
    fun invalidate() {
        clients?.strict?.dispatcher?.cancelAll()
        clients?.strict?.connectionPool?.evictAll()
        clients = null
    }

    @Synchronized
    override fun newCall(request: Request): Call {
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
            clients = Clients(selected.key, strict, selfSigned)
        }
        val current = checkNotNull(clients)
        val permitted = selected.usesTls && selected.allowSelfSigned && request.url.isHttps &&
            request.url.host.equals(selected.hostname, ignoreCase = true)
        return (if (permitted) current.selfSigned else current.strict).newCall(request)
    }
}

object InsecureHttp {
    private val clients = ServerHttpClients { ServerSettings.config }
    val client: Call.Factory = clients
    fun reset() = clients.invalidate()
}
