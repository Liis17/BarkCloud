package com.barkfluff.BarkCloud.grpc

import io.grpc.CallOptions
import io.grpc.Channel
import io.grpc.ClientCall
import io.grpc.Metadata
import io.grpc.MethodDescriptor
import io.mockk.every
import io.mockk.mockk
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Test
import java.lang.reflect.Constructor

class ClientMetadataInterceptorTest {

    private fun headersAfterStart(): Metadata {
        // Используем рефлексию для приватного конструктора — реальный create() требует
        // Android Context, поэтому статические значения подсовываем напрямую.
        val ctor: Constructor<*> = ClientMetadataInterceptor::class.java.declaredConstructors.first()
        ctor.isAccessible = true
        val interceptor = ctor.newInstance(
            "deviceId-b64",
            "deviceName-b64",
            "osName-b64",
            "appName-b64",
            "appVersion-b64",
            "ip-b64",
        ) as ClientMetadataInterceptor

        val mockCall = mockk<ClientCall<Any, Any>>(relaxed = true)
        val mockChannel = mockk<Channel>()
        every { mockChannel.newCall<Any, Any>(any(), any()) } returns mockCall
        val method = mockk<MethodDescriptor<Any, Any>>(relaxed = true)
        val listener = mockk<ClientCall.Listener<Any>>(relaxed = true)

        val call = interceptor.interceptCall(method, CallOptions.DEFAULT, mockChannel)
        val headers = Metadata()
        call.start(listener, headers)
        return headers
    }

    private fun key(name: String) =
        Metadata.Key.of(name, Metadata.ASCII_STRING_MARSHALLER)

    @Test
    fun `interceptor populates all six client metadata headers`() {
        val headers = headersAfterStart()

        assertEquals("deviceId-b64", headers.get(key("x-device-id")))
        assertEquals("deviceName-b64", headers.get(key("x-device-name")))
        assertEquals("osName-b64", headers.get(key("x-os-name")))
        assertEquals("appName-b64", headers.get(key("x-app-name")))
        assertEquals("appVersion-b64", headers.get(key("x-app-version")))
        assertEquals("ip-b64", headers.get(key("x-ip-address")))
    }

    @Test
    fun `intercepted call wraps the underlying channel`() {
        val headers = headersAfterStart()

        // Если интерцептор бы упал на старте (например, попытался читать Settings.Secure),
        // headers были бы пусты — этот тест служит smoke-проверкой.
        assertNotNull(headers.get(key("x-device-id")))
    }
}
