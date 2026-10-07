package com.barkfluff.BarkCloud.data

import barkcloud.identity.IdentityApiGrpcKt.IdentityApiCoroutineStub
import barkcloud.identity.IdentityApiOuterClass.*
import com.barkfluff.BarkCloud.grpc.GrpcManager
import com.google.protobuf.Timestamp
import io.grpc.*
import io.mockk.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

class AuthFlowRepositoryTest {
    private val public = mockk<IdentityApiCoroutineStub>()
    private val registration = mockk<IdentityApiCoroutineStub>()
    private val global = mockk<GlobalParam>(relaxed = true)
    private val grpc = mockk<GrpcManager> {
        every { publicIdentityStub() } returns public
        every { registrationIdentityStub("access") } returns registration
    }
    private val store = object : PendingRegistrationStorage {
        var value: PendingRegistration? = null
        override fun read() = value
        override fun save(pending: PendingRegistration) { value = pending }
        override fun clear() { value = null }
    }
    private var serverKey = "server"
    private val repository = AuthFlowRepository(grpc, global, store, { serverKey }, { 1000 })
    private val details = RegistrationDetails("Alice", "", "alice", "alice@example.com")
    private fun token(value: String) = Token.newBuilder().setValue(value)
        .setExpirationDate(Timestamp.newBuilder().setSeconds(10000)).build()
    init {
        every { public.withDeadlineAfter(any(), any()) } returns public
        every { registration.withDeadlineAfter(any(), any()) } returns registration
    }
    private fun pending() { store.save(PendingRegistration("server", details, "refresh", 10_000_000)) }
    private fun error(code: String) = Status.UNKNOWN.asRuntimeException(Metadata().apply {
        put(Metadata.Key.of("x-error-code", Metadata.ASCII_STRING_MARSHALLER), code)
    })
    @Test fun `create without email stores pending refresh without opening session`() = runTest {
        coEvery { public.createAccount(any(), any()) } returns CreateAccountResponse.newBuilder().setRefreshToken(token("refresh")).build()
        val result = repository.createAccount(details)
        assertTrue(result.needsPassword); assertNull(result.codeId)
        assertEquals("refresh", store.read()!!.refreshToken)
        verify(exactly = 0) { global.saveTokens(any(), any(), any(), any()) }
    }
    @Test fun `mail signup confirmation is durable before setting password`() = runTest {
        coEvery { public.createAccount(any(), any()) } returns CreateAccountResponse.newBuilder().setCodeId("challenge").build()
        coEvery { public.confirmAccount(any(), any()) } returns ConfirmAccountResponse.newBuilder().setRefreshToken(token("refresh")).build()
        assertEquals("challenge", repository.createAccount(details).codeId)
        assertNull(store.read())
        repository.confirmAccount(details, "challenge", "123456")
        assertNotNull(store.read())
        verify(exactly = 0) { global.saveTokens(any(), any(), any(), any()) }
    }
    @Test fun `session becomes active only after password was installed`() = runTest {
        pending()
        coEvery { public.createToken(any(), any()) } returns CreateTokenResponse.newBuilder().setAccessToken(token("access")).build()
        coEvery { registration.setPassword(any(), any()) } coAnswers {
            assertNotNull(store.read())
            verify(exactly = 0) { global.saveTokens(any(), any(), any(), any()) }
            SetPasswordResponse.getDefaultInstance()
        }
        repository.finishRegistration("password123")
        verify { global.saveTokens("access", 10_000_000, "refresh", 10_000_000) }
        assertNull(store.read())
    }
    @Test fun `unknown password result verifies login instead of replaying confirmation`() = runTest {
        pending()
        coEvery { public.createToken(any(), any()) } returns CreateTokenResponse.newBuilder().setAccessToken(token("access")).build()
        coEvery { registration.setPassword(any(), any()) } throws Status.DEADLINE_EXCEEDED.asRuntimeException()
        coEvery { public.auth(any(), any()) } returns AuthResponse.newBuilder().setAccessToken(token("newaccess")).setRefreshToken(token("newrefresh")).build()
        repository.finishRegistration("password123")
        coVerify { public.auth(match { it.email == details.email && it.password == "password123" }, any()) }
        coVerify(exactly = 0) { public.confirmAccount(any(), any()) }
        verify { global.saveTokens("newaccess", 10_000_000, "newrefresh", 10_000_000) }
        assertNull(store.read())
    }
    @Test fun `invalid old password is not falsely treated as success`() = runTest {
        pending()
        coEvery { public.createToken(any(), any()) } returns CreateTokenResponse.newBuilder().setAccessToken(token("access")).build()
        coEvery { registration.setPassword(any(), any()) } throws error("A7E3F1B2-9C4D-4E8A-B5F6-2D1A3C7E9F04")
        coEvery { public.auth(any(), any()) } throws error("21BFB9B5-C377-45D1-9B15-6B7F3432B397")
        try { repository.finishRegistration("wrongpass"); fail("Must fail") } catch (_: AuthFlowException) { }
        assertNotNull(store.read())
        verify(exactly = 0) { global.saveTokens(any(), any(), any(), any()) }
    }
    @Test fun `pending token is scoped to server and expiration`() {
        pending(); serverKey = "different"; assertNull(repository.pendingRegistration()); assertNull(store.read())
        store.save(PendingRegistration("different", details, "refresh", 999))
        assertNull(repository.pendingRegistration()); assertNull(store.read())
    }
    @Test fun `reset sends code password and revoke preference together then saves session`() = runTest {
        coEvery { public.confirmResetPassword(any(), any()) } returns ConfirmResetPasswordResponse.newBuilder()
            .setAccessToken(token("access")).setRefreshToken(token("refresh")).build()
        repository.confirmPasswordReset("reset", "123456", "password123", false)
        coVerify { public.confirmResetPassword(match { it.resetId == "reset" && it.otpCode == "123456" && it.newPassword == "password123" && !it.revokeOtherSessions }, any()) }
        verify { global.saveTokens("access", 10_000_000, "refresh", 10_000_000) }
    }
    @Test fun `cancellation is propagated instead of shown as form error`() = runTest {
        coEvery { public.resetPassword(any(), any()) } throws CancellationException("cancel")
        try { repository.requestPasswordReset("alice"); fail("Must cancel") } catch (_: CancellationException) { }
    }
    @Test fun `checked coroutine failure preserves invalid code and retry after`() = runTest {
        val trailers = Metadata().apply {
            put(Metadata.Key.of("x-error-code", Metadata.ASCII_STRING_MARSHALLER), "803B632C-4457-4B05-9435-9C3DD0F41E00")
            put(Metadata.Key.of("x-retry-after-seconds", Metadata.ASCII_STRING_MARSHALLER), "120")
        }
        coEvery { public.confirmResetPassword(any(), any()) } throws Status.FAILED_PRECONDITION.asException(trailers)
        try { repository.confirmPasswordReset("id", "123456", "password123", true); fail("Must fail") }
        catch (e: AuthFlowException) { assertEquals(AuthFailureKind.INVALID_CODE, e.kind); assertEquals(120, e.retryAfterSeconds) }
        verify(exactly = 0) { global.saveTokens(any(), any(), any(), any()) }
    }

}
