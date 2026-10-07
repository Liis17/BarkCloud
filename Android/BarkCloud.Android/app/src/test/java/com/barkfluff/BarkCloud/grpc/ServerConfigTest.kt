package com.barkfluff.BarkCloud.grpc

import org.junit.Assert.*
import org.junit.Test

class ServerConfigTest {
    private fun validate(host: String = "example.com", port: String = "8000", trust: Boolean = true) =
        validateServerConfig(host, port, "8001", "8005", trust)
    @Test fun `normalizes domain and trailing slash`() {
        assertEquals("https://example.com", validate(" EXAMPLE.com/ ").config!!.host)
    }
    @Test fun `accepts IPv4 and IPv6`() {
        assertEquals("https://127.0.0.1:8000", validate("127.0.0.1").config!!.address(8000))
        assertEquals("https://[::1]:8000", validate("https://[::1]").config!!.address(8000))
    }
    @Test fun `rejects embedded ports paths credentials and unsupported schemes`() {
        listOf("", "https://host:8000", "https://host/path", "https://user@host", "ftp://host", "https://host?q=1", "https://host#x", "a b").forEach {
            assertNotNull(it, validate(it).error)
        }
    }
    @Test fun `rejects invalid ports without fallback`() {
        listOf("", "0", "65536", "-1", "abc", "999999999999999").forEach { assertNull(validate(port = it).config) }
        assertNotNull(validate(port = "1").config)
        assertNotNull(validate(port = "65535").config)
    }
    @Test fun `HTTP cannot grant certificate exception`() { assertFalse(validate("http://localhost").config!!.allowSelfSigned) }
}
