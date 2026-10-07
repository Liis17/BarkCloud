package com.barkfluff.BarkCloud.data

import org.junit.Assert.*
import org.junit.Test
import io.grpc.*

class AuthValidationTest {
    @Test fun `new passwords count unicode characters and UTF8 bytes`() {
        assertNotNull(newPasswordError("abcdefg"))
        assertNull(newPasswordError("abcdefgh"))
        assertNull(newPasswordError("я".repeat(36)))
        assertNotNull(newPasswordError("я".repeat(37)))
        assertNull(newPasswordError("😀".repeat(8)))
        assertNotNull(newPasswordError("😀".repeat(19)))
    }
    @Test fun `paste accepts only six ASCII code digits`() {
        assertEquals("123456", verificationDigits("12 3-456789"))
        assertEquals("12", verificationDigits("١２12"))
    }
    @Test fun `rate limit reads retry trailer`() {
        val metadata = Metadata().apply {
            put(Metadata.Key.of("x-error-code", Metadata.ASCII_STRING_MARSHALLER), "8F2B6D41-5A93-4C7E-B0D8-1E4A7C9F3B26")
            put(Metadata.Key.of("x-retry-after-seconds", Metadata.ASCII_STRING_MARSHALLER), "120")
        }
        val error = Status.RESOURCE_EXHAUSTED.asRuntimeException(metadata).toAuthFlowException()
        assertEquals(AuthFailureKind.RATE_LIMIT, error.kind)
        assertEquals(120, error.retryAfterSeconds)
    }
}
