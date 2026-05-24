package com.barkfluff.BarkCloud.grpc

import barkcloud.identity.IdentityApiGrpcKt
import com.barkfluff.BarkCloud.BuildConfig
import com.barkfluff.BarkCloud.data.GlobalParam
import io.grpc.ClientInterceptors
import io.grpc.ManagedChannel
import io.grpc.okhttp.OkHttpChannelBuilder
import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager

class GrpcManager(
    private val globalParam: GlobalParam,
    private val metadataInterceptor: ClientMetadataInterceptor,
) {

    @Volatile
    private var identityChannel: ManagedChannel? = null

    @Volatile
    private var identityStub: IdentityApiGrpcKt.IdentityApiCoroutineStub? = null

    fun identityStub(): IdentityApiGrpcKt.IdentityApiCoroutineStub {
        identityStub?.let { return it }
        synchronized(this) {
            identityStub?.let { return it }
            val channel = createChannel(BuildConfig.IDENTITY_API_ADDRESS)
            val intercepted = ClientInterceptors.intercept(
                channel, AuthInterceptor(globalParam), metadataInterceptor
            )
            val stub = IdentityApiGrpcKt.IdentityApiCoroutineStub(intercepted)
            identityChannel = channel
            identityStub = stub
            return stub
        }
    }

    fun shutdown() {
        identityChannel?.shutdownNow()
        identityChannel = null
        identityStub = null
    }

    private fun createChannel(address: String): ManagedChannel {
        val url = ensureScheme(address)
        val useTls = url.startsWith("https://")
        val hostPort = url.removePrefix("http://").removePrefix("https://")
        val parts = hostPort.split(":")
        val host = parts[0]
        val port = parts.getOrNull(1)?.toIntOrNull() ?: if (useTls) 443 else 80

        val builder = OkHttpChannelBuilder.forAddress(host, port)
        if (useTls) {
            // Сервер использует самоподписанный сертификат — доверяем всем.
            val trustManager = object : X509TrustManager {
                override fun checkClientTrusted(chain: Array<X509Certificate>, authType: String) = Unit
                override fun checkServerTrusted(chain: Array<X509Certificate>, authType: String) = Unit
                override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
            }
            val sslContext = SSLContext.getInstance("TLS")
            sslContext.init(null, arrayOf<TrustManager>(trustManager), null)
            builder.sslSocketFactory(sslContext.socketFactory)
        } else {
            builder.usePlaintext()
        }
        return builder.build()
    }

    private fun ensureScheme(url: String): String =
        if (url.startsWith("http://") || url.startsWith("https://")) url else "https://$url"
}
