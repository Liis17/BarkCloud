package com.barkfluff.BarkCloud.net

import com.barkfluff.BarkCloud.grpc.ServerConfig
import okhttp3.Request
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.tls.HandshakeCertificates
import okhttp3.tls.HeldCertificate
import org.junit.Assert.*
import org.junit.Test
import java.io.IOException

class ServerHttpClientsTest {
    private fun server(): MockWebServer {
        val cert = HeldCertificate.Builder().commonName("localhost").addSubjectAlternativeName("localhost").build()
        val tls = HandshakeCertificates.Builder().heldCertificate(cert).build()
        return MockWebServer().apply { useHttps(tls.sslSocketFactory(), false); start() }
    }
    @Test fun `trust follows selected host policy and hostname validation remains`() {
        server().use { server ->
            var config = ServerConfig("https://localhost")
            val client = ServerHttpClients { config }
            val request = Request.Builder().url(server.url("/")).build()
            assertThrows(IOException::class.java) { client.newCall(request).execute().close() }
            config = config.copy(allowSelfSigned = true)
            server.enqueue(MockResponse().setBody("ok"))
            client.newCall(request).execute().use { assertEquals("ok", it.body!!.string()) }
            // The exception is scoped to the hostname, and certificate names are still checked.
            val wrongName = request.newBuilder().url(server.url("/").newBuilder().host("127.0.0.1").build()).build()
            config = ServerConfig("https://127.0.0.1", allowSelfSigned = true)
            assertThrows(IOException::class.java) { client.newCall(wrongName).execute().close() }
            config = ServerConfig("https://other.example", allowSelfSigned = true)
            assertThrows(IOException::class.java) { client.newCall(request).execute().close() }
            config = ServerConfig("https://localhost", allowSelfSigned = false)
            assertThrows(IOException::class.java) { client.newCall(request).execute().close() }
        }
    }
    @Test fun `trusted host cannot redirect its certificate exception to another host`() {
        server().use { selected -> server().use { external ->
            val client = ServerHttpClients { ServerConfig("https://localhost", allowSelfSigned = true) }
            selected.enqueue(MockResponse().setResponseCode(302).addHeader("Location", external.url("/").newBuilder().host("127.0.0.1").build()))
            client.newCall(Request.Builder().url(selected.url("/")).build()).execute().use { assertEquals(302, it.code) }
            assertEquals(0, external.requestCount)
        } }
    }
}
