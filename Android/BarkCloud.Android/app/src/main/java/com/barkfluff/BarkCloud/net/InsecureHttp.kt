package com.barkfluff.BarkCloud.net

import okhttp3.OkHttpClient
import java.util.concurrent.TimeUnit
import javax.net.ssl.HostnameVerifier

/**
 * OkHttp-клиент, доверяющий самоподписанному сертификату сервера. Используется для
 * multipart-загрузки/скачивания оригиналов файлов ([FileTransferService]) и как
 * call-factory для сетевого слоя Coil (превью с :7025).
 */
object InsecureHttp {

    val client: OkHttpClient by lazy {
        OkHttpClient.Builder()
            .sslSocketFactory(InsecureTls.socketFactory(), InsecureTls.trustManager)
            .hostnameVerifier(HostnameVerifier { _, _ -> true })
            .connectTimeout(60, TimeUnit.SECONDS)
            .readTimeout(120, TimeUnit.SECONDS)
            .writeTimeout(600, TimeUnit.SECONDS)
            .callTimeout(600, TimeUnit.SECONDS)
            .build()
    }
}
