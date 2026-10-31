package com.barkfluff.BarkCloud.data.update

import kotlinx.coroutines.runBlocking
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class GitHubReleaseClientTest {
    @Test
    fun honorsRetryAfterAndSendsNoCloudAuthentication() = runBlocking {
        val server = MockWebServer()
        server.enqueue(MockResponse().setResponseCode(429).addHeader("Retry-After", "30"))
        server.start()
        try {
            val client = GitHubReleaseClient(
                client = OkHttpClient(),
                nowMillis = { 1_000_000L },
                apiUrl = server.url("/repos/Liis17/BarkCloud/releases/latest").toString(),
            )
            val error = try {
                client.fetchLatest()
                throw AssertionError("Expected GitHub rate limit")
            } catch (expected: GitHubRateLimitException) {
                expected
            }
            val request = server.takeRequest()

            assertEquals(1_030_000L, error.retryAtMillis)
            assertEquals("application/vnd.github+json", request.getHeader("Accept"))
            assertEquals("BarkCloud-Android-Updater", request.getHeader("User-Agent"))
            assertNull(request.getHeader("Authorization"))
        } finally {
            server.shutdown()
        }
    }

    @Test
    fun usesGitHubRateLimitResetHeaderWhenRetryAfterIsMissing() = runBlocking {
        val server = MockWebServer()
        server.enqueue(MockResponse().setResponseCode(403).addHeader("X-RateLimit-Reset", "1700000123"))
        server.start()
        try {
            val client = GitHubReleaseClient(
                client = OkHttpClient(),
                nowMillis = { 1_000_000L },
                apiUrl = server.url("/latest").toString(),
            )
            val error = try {
                client.fetchLatest()
                throw AssertionError("Expected GitHub rate limit")
            } catch (expected: GitHubRateLimitException) {
                expected
            }

            assertEquals(1_700_000_123_000L, error.retryAtMillis)
        } finally {
            server.shutdown()
        }
    }
}
