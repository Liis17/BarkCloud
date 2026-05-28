package com.barkfluff.BarkCloud.data

import barkcloud.identity.IdentityApiGrpcKt
import barkcloud.identity.IdentityApiOuterClass.AuthRequest
import barkcloud.identity.IdentityApiOuterClass.AuthResponse
import barkcloud.identity.IdentityApiOuterClass.LogoutRequest
import barkcloud.identity.IdentityApiOuterClass.Token
import com.barkfluff.BarkCloud.grpc.GrpcManager
import com.google.protobuf.Timestamp
import io.grpc.Metadata
import io.grpc.Status
import io.grpc.StatusRuntimeException
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.every
import io.mockk.mockk
import io.mockk.slot
import io.mockk.verify
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class AuthRepositoryTest {

    private val stub = mockk<IdentityApiGrpcKt.IdentityApiCoroutineStub>()
    private val grpcManager = mockk<GrpcManager> { every { identityStub() } returns stub }
    private val globalParam = mockk<GlobalParam>(relaxed = true)
    private val repository = AuthRepository(grpcManager, globalParam)

    private val authError = "21BFB9B5-C377-45D1-9B15-6B7F3432B397"
    private val otpError = "C1576884-12D8-4722-A7EE-9F9789AD1265"

    private fun trailerWith(code: String): Metadata = Metadata().apply {
        put(Metadata.Key.of("x-error-code", Metadata.ASCII_STRING_MARSHALLER), code)
    }

    private fun successResponse(): AuthResponse = AuthResponse.newBuilder()
        .setAccessToken(Token.newBuilder()
            .setValue("ACCESS")
            .setExpirationDate(Timestamp.newBuilder().setSeconds(1_700_000_000).build())
            .build())
        .setRefreshToken(Token.newBuilder()
            .setValue("REFRESH")
            .setExpirationDate(Timestamp.newBuilder().setSeconds(1_700_010_000).build())
            .build())
        .build()

    @Test
    fun `auth treats login with @ as email`() = runTest {
        val request = slot<AuthRequest>()
        coEvery { stub.auth(capture(request), any()) } returns successResponse()

        repository.auth("foo@bar", "pwd")

        assertEquals("foo@bar", request.captured.email)
        assertEquals("", request.captured.username)
    }

    @Test
    fun `auth treats login without @ as username`() = runTest {
        val request = slot<AuthRequest>()
        coEvery { stub.auth(capture(request), any()) } returns successResponse()

        repository.auth("john", "pwd")

        assertEquals("john", request.captured.username)
        assertEquals("", request.captured.email)
    }

    @Test
    fun `auth includes otpCode when provided`() = runTest {
        val request = slot<AuthRequest>()
        coEvery { stub.auth(capture(request), any()) } returns successResponse()

        repository.auth("john", "pwd", otpCode = "123456")

        assertEquals("123456", request.captured.otpCode)
    }

    @Test
    fun `auth omits otpCode when blank`() = runTest {
        val request = slot<AuthRequest>()
        coEvery { stub.auth(capture(request), any()) } returns successResponse()

        repository.auth("john", "pwd", otpCode = "  ")

        assertEquals("", request.captured.otpCode)
    }

    @Test
    fun `auth success persists tokens and returns Success`() = runTest {
        coEvery { stub.auth(any(), any()) } returns successResponse()

        val result = repository.auth("john", "pwd")

        assertEquals(AuthResult.Success, result)
        verify { globalParam.accessToken = "ACCESS" }
        verify { globalParam.refreshToken = "REFRESH" }
        verify { globalParam.accessTokenExpiresAt = 1_700_000_000_000L }
    }

    @Test
    fun `auth maps OTP_REQUIRED error to OtpRequired`() = runTest {
        coEvery { stub.auth(any(), any()) } throws StatusRuntimeException(Status.UNKNOWN, trailerWith(otpError))

        val result = repository.auth("john", "pwd")

        assertEquals(AuthResult.OtpRequired, result)
    }

    @Test
    fun `auth maps INVALID_CREDENTIALS error to InvalidCredentials`() = runTest {
        coEvery { stub.auth(any(), any()) } throws StatusRuntimeException(Status.UNKNOWN, trailerWith(authError))

        val result = repository.auth("john", "pwd")

        assertEquals(AuthResult.InvalidCredentials, result)
    }

    @Test
    fun `auth maps other grpc error to OtherError`() = runTest {
        coEvery { stub.auth(any(), any()) } throws StatusRuntimeException(
            Status.UNAVAILABLE.withDescription("server down"))

        val result = repository.auth("john", "pwd")

        assertTrue(result is AuthResult.OtherError)
    }

    @Test
    fun `auth maps generic exception to OtherError`() = runTest {
        coEvery { stub.auth(any(), any()) } throws RuntimeException("boom")

        val result = repository.auth("john", "pwd")

        assertEquals(AuthResult.OtherError("boom"), result)
    }

    @Test
    fun `logout calls grpc logout and ignores errors`() = runTest {
        coEvery { stub.logout(any<LogoutRequest>(), any()) } throws RuntimeException("ignored")

        repository.logout()

        coVerify { stub.logout(any<LogoutRequest>(), any()) }
    }
}
