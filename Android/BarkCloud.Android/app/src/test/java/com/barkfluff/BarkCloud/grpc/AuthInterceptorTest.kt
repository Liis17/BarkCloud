package com.barkfluff.BarkCloud.grpc

import com.barkfluff.BarkCloud.data.GlobalParam
import io.grpc.CallOptions
import io.grpc.Channel
import io.grpc.ClientCall
import io.grpc.Metadata
import io.grpc.MethodDescriptor
import io.mockk.every
import io.mockk.mockk
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class AuthInterceptorTest {

    private val authKey: Metadata.Key<String> =
        Metadata.Key.of("x-auth-token", Metadata.ASCII_STRING_MARSHALLER)

    private fun setup(token: String?): Metadata {
        val globalParam = mockk<GlobalParam>()
        every { globalParam.accessToken } returns token

        val mockCall = mockk<ClientCall<Any, Any>>(relaxed = true)
        val mockChannel = mockk<Channel>()
        every { mockChannel.newCall<Any, Any>(any(), any()) } returns mockCall

        val method = mockk<MethodDescriptor<Any, Any>>(relaxed = true)
        val listener = mockk<ClientCall.Listener<Any>>(relaxed = true)

        val interceptor = AuthInterceptor(globalParam)
        val call = interceptor.interceptCall(method, CallOptions.DEFAULT, mockChannel)
        val headers = Metadata()
        call.start(listener, headers)
        return headers
    }

    @Test
    fun `adds x-auth-token when token is present`() {
        val headers = setup("ACCESS-123")
        assertEquals("ACCESS-123", headers.get(authKey))
    }

    @Test
    fun `does not add x-auth-token when token is null`() {
        val headers = setup(null)
        assertNull(headers.get(authKey))
    }

    @Test
    fun `does not add x-auth-token when token is blank`() {
        val headers = setup("   ")
        assertNull(headers.get(authKey))
    }
}
