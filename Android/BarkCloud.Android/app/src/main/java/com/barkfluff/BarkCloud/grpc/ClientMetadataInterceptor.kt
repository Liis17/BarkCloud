package com.barkfluff.BarkCloud.grpc

import android.annotation.SuppressLint
import android.content.Context
import android.os.Build
import android.provider.Settings
import android.util.Base64
import com.barkfluff.BarkCloud.BuildConfig
import io.grpc.CallOptions
import io.grpc.Channel
import io.grpc.ClientCall
import io.grpc.ClientInterceptor
import io.grpc.ForwardingClientCall
import io.grpc.Metadata
import io.grpc.MethodDescriptor
import java.net.Inet4Address
import java.net.NetworkInterface

/**
 * Добавляет в каждый запрос метаданные клиента, которые ожидает сервер:
 * x-device-id, x-device-name, x-os-name, x-app-name, x-app-version, x-ip-address.
 * Значения статичны (считаются один раз) и кодируются в base64 — сервер делает
 * Convert.FromBase64String. Обязательно NO_WRAP: перенос строки сломал бы заголовок.
 *
 * x-auth-token добавляется отдельно в [AuthInterceptor], т.к. он динамический.
 */
class ClientMetadataInterceptor private constructor(
    private val deviceId: String,
    private val deviceName: String,
    private val osName: String,
    private val appName: String,
    private val appVersion: String,
    private val ipAddress: String,
) : ClientInterceptor {

    override fun <ReqT : Any, RespT : Any> interceptCall(
        method: MethodDescriptor<ReqT, RespT>,
        callOptions: CallOptions,
        next: Channel,
    ): ClientCall<ReqT, RespT> {
        return object : ForwardingClientCall.SimpleForwardingClientCall<ReqT, RespT>(
            next.newCall(method, callOptions)
        ) {
            override fun start(responseListener: Listener<RespT>, headers: Metadata) {
                headers.put(DEVICE_ID_KEY, deviceId)
                headers.put(DEVICE_NAME_KEY, deviceName)
                headers.put(OS_NAME_KEY, osName)
                headers.put(APP_NAME_KEY, appName)
                headers.put(APP_VERSION_KEY, appVersion)
                headers.put(IP_ADDRESS_KEY, ipAddress)
                super.start(responseListener, headers)
            }
        }
    }

    companion object {
        @SuppressLint("HardwareIds")
        fun create(context: Context): ClientMetadataInterceptor {
            val androidId = Settings.Secure.getString(
                context.contentResolver, Settings.Secure.ANDROID_ID
            ).orEmpty()
            val version = BuildConfig.VERSION_NAME
            return ClientMetadataInterceptor(
                deviceId = encode(androidId),
                deviceName = encode("${Build.MANUFACTURER} ${Build.MODEL}"),
                osName = encode("Android ${Build.VERSION.RELEASE}"),
                appName = encode("BarkCloud v.$version"),
                appVersion = encode(version),
                ipAddress = encode(firstNonLoopbackIPv4().orEmpty()),
            )
        }

        private fun encode(raw: String): String =
            Base64.encodeToString(raw.toByteArray(Charsets.UTF_8), Base64.NO_WRAP)

        private fun firstNonLoopbackIPv4(): String? = runCatching {
            NetworkInterface.getNetworkInterfaces().asSequence()
                .filter { it.isUp && !it.isLoopback }
                .flatMap { it.inetAddresses.asSequence() }
                .filterIsInstance<Inet4Address>()
                .firstOrNull { !it.isLoopbackAddress }
                ?.hostAddress
        }.getOrNull()

        private fun key(name: String): Metadata.Key<String> =
            Metadata.Key.of(name, Metadata.ASCII_STRING_MARSHALLER)

        private val DEVICE_ID_KEY = key("x-device-id")
        private val DEVICE_NAME_KEY = key("x-device-name")
        private val OS_NAME_KEY = key("x-os-name")
        private val APP_NAME_KEY = key("x-app-name")
        private val APP_VERSION_KEY = key("x-app-version")
        private val IP_ADDRESS_KEY = key("x-ip-address")
    }
}
