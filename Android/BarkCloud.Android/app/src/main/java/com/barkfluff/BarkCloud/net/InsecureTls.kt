package com.barkfluff.BarkCloud.net

import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocketFactory
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager

/**
 * Certificate-chain exception used only after the caller selects allowSelfSigned for its host.
 * Both transports retain their default hostname verifier; external HTTP hosts use strict TLS.
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
