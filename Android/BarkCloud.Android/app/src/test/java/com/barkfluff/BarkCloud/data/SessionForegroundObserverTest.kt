package com.barkfluff.BarkCloud.data

import androidx.lifecycle.LifecycleOwner
import io.mockk.every
import io.mockk.mockk
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Test

class SessionForegroundObserverTest {
    @Test
    fun `return from background refreshes a token that expired while away`() = runTest {
        val session = mockk<GlobalParam>()
        var token = "initial"
        var expiration = Long.MAX_VALUE
        every { session.accessToken } answers { token }
        every { session.accessTokenExpiresAt } answers { expiration }
        every { session.refreshToken } returns "refresh"
        every { session.saveRefreshedAccessToken(any(), any()) } answers {
            token = firstArg()
            expiration = secondArg()
            Unit
        }
        var calls = 0
        val refresher = TokenRefresher(session) {
            calls++
            TokenRefresher.RefreshedAccessToken("renewed", Long.MAX_VALUE)
        }
        val observer = SessionForegroundObserver(this) { refresher.validAccessToken(); Unit }
        val owner = mockk<LifecycleOwner>()
        observer.onStart(owner)
        runCurrent()
        assertEquals(0, calls)

        observer.onStop(owner)
        expiration = 0L
        observer.onStart(owner)
        runCurrent()

        assertEquals("renewed", token)
        assertEquals(1, calls)
    }

    @Test
    fun `network failure allows retry on next foreground`() = runTest {
        var attempts = 0
        val observer = SessionForegroundObserver(this) {
            attempts++
            if (attempts == 1) throw java.io.IOException("offline")
        }
        val owner = mockk<LifecycleOwner>()
        observer.onStart(owner)
        runCurrent()
        observer.onStop(owner)
        observer.onStart(owner)
        runCurrent()
        assertEquals(2, attempts)
    }
}
