package com.barkfluff.BarkCloud.net

import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocketFactory
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager

/**
 * Сервер BarkCloud за nginx использует самоподписанный TLS-сертификат. Здесь общий
 * «доверяй всем» TrustManager и фабрика сокетов — их разделяют gRPC-каналы
 * ([com.barkfluff.BarkCloud.grpc.GrpcManager]) и HTTP-клиент для upload/download и
 * превью ([InsecureHttp]).
 */
object InsecureTls {

    val trustManager: X509TrustManager = object : X509TrustManager {
        override fun checkClientTrusted(chain: Array<X509Certificate>, authType: String) = Unit
        override fun checkServerTrusted(chain: Array<X509Certificate>, authType: String) = Unit
        override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
    }

    fun socketFactory(): SSLSocketFactory {
        val ctx = SSLContext.getInstance("TLS")
        ctx.init(null, arrayOf<TrustManager>(trustManager), null)
        return ctx.socketFactory
    }
}
