package com.barkfluff.BarkCloud.data

import io.grpc.Metadata
import io.grpc.Status
import io.grpc.StatusRuntimeException
import io.mockk.every
import io.mockk.just
import io.mockk.mockk
import io.mockk.runs
import io.mockk.verify
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.delay
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.concurrent.atomic.AtomicInteger

class TokenRefresherTest {

    @Test
    fun `expired access token is refreshed once for concurrent callers`() = runTest {
        val session = mockk<GlobalParam>()
        var access = "expired"
        var expiresAt = 0L
        every { session.accessToken } answers { access }
        every { session.accessTokenExpiresAt } answers { expiresAt }
        every { session.refreshToken } returns "refresh"
        every { session.saveRefreshedAccessToken(any(), any()) } answers {
            access = firstArg()
            expiresAt = secondArg()
            Unit
        }

        val calls = AtomicInteger(0)
        val refresher = TokenRefresher(session) {
            calls.incrementAndGet()
            delay(20)
            TokenRefresher.RefreshedAccessToken("fresh", Long.MAX_VALUE)
        }

        val values = listOf(
            async { refresher.validAccessToken() },
            async { refresher.validAccessToken() },
            async { refresher.validAccessToken() },
        ).awaitAll()

        assertEquals(listOf("fresh", "fresh", "fresh"), values)
        assertEquals(1, calls.get())
    }

    @Test
    fun `invalid refresh clears the local session`() = runTest {
        val session = mockk<GlobalParam>()
        every { session.accessToken } returns "expired"
        every { session.accessTokenExpiresAt } returns 0L
        every { session.refreshToken } returns "refresh"
        every { session.clearSession() } just runs
        val trailers = Metadata().apply {
            put(Metadata.Key.of("x-error-code", Metadata.ASCII_STRING_MARSHALLER), "7E6A31C5-3C4D-412E-87BC-0A387617A5D3")
        }
        val refresher = TokenRefresher(session) {
            throw StatusRuntimeException(Status.FAILED_PRECONDITION, trailers)
        }

        val result = runCatching { refresher.validAccessToken() }

        assertTrue(result.isFailure)
        verify { session.clearSession() }
    }
}
