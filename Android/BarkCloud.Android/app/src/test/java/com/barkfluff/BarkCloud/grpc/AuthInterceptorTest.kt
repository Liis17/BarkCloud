package com.barkfluff.BarkCloud.grpc

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

    private fun setup(token: String?, methodName: String = "barkcloud.files.CloudApi/ListDirectory"): Metadata {
        val mockCall = mockk<ClientCall<Any, Any>>(relaxed = true)
        val mockChannel = mockk<Channel>()
        every { mockChannel.newCall<Any, Any>(any(), any()) } returns mockCall

        val method = mockk<MethodDescriptor<Any, Any>>()
        every { method.fullMethodName } returns methodName
        val listener = mockk<ClientCall.Listener<Any>>(relaxed = true)

        val interceptor = AuthInterceptor { token }
        val call = interceptor.interceptCall(method, CallOptions.DEFAULT, mockChannel)
        val headers = Metadata()
        call.start(listener, headers)
        return headers
    }

    @Test
    fun `adds refreshed token to authenticated call`() {
        assertEquals("ACCESS-123", setup("ACCESS-123").get(authKey))
    }

    @Test
    fun `does not add header when token provider has no session`() {
        assertNull(setup(null).get(authKey))
    }

    @Test
    fun `Auth and CreateToken never receive a stale token`() {
        assertNull(setup("STALE", "barkcloud.identity.IdentityApi/Auth").get(authKey))
        assertNull(setup("STALE", "barkcloud.identity.IdentityApi/CreateToken").get(authKey))
    }
}
