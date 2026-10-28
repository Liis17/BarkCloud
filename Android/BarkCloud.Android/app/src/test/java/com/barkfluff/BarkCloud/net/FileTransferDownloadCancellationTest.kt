package com.barkfluff.BarkCloud.net

import android.content.Context
import com.barkfluff.BarkCloud.grpc.GrpcManager
import io.mockk.every
import io.mockk.mockk
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import okhttp3.Call
import okhttp3.EventListener
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.io.IOException
import java.nio.file.Files
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

class FileTransferDownloadCancellationTest {
    @Test
    fun cancellingDownloadClosesTheCallAndAllowsANewDownload() = runBlocking {
        val server = MockWebServer()
        val cacheDir = Files.createTempDirectory("download-cancel-").toFile()
        val context = mockk<Context>()
        every { context.cacheDir } returns cacheDir
        val callFailed = CountDownLatch(1)
        val client = OkHttpClient.Builder().eventListener(object : EventListener() {
            override fun callFailed(call: Call, ioe: IOException) {
                callFailed.countDown()
            }
        }).build()
        val service = FileTransferService(context, mockk<GrpcManager>(relaxed = true), client)
        server.enqueue(MockResponse().setHeadersDelay(1, TimeUnit.SECONDS).setBody("slow"))
        server.enqueue(MockResponse().setBody("ok"))
        server.start()
        try {
            val request = launch(Dispatchers.IO) {
                service.download(server.url("/slow").toString(), "cancelled.tmp")
            }
            assertNotNull(withContext(Dispatchers.IO) { server.takeRequest(5, TimeUnit.SECONDS) })

            request.cancelAndJoin()

            assertTrue("OkHttp did not report the cancelled download", callFailed.await(5, TimeUnit.SECONDS))
            val retry = service.download(server.url("/retry").toString(), "retry.tmp")
            assertEquals("ok", retry.readText())
            assertTrue(retry.isFile)
        } finally {
            server.shutdown()
            cacheDir.deleteRecursively()
        }
    }
}
