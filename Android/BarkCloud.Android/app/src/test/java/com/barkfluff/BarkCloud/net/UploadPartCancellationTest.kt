package com.barkfluff.BarkCloud.net

import android.content.ContentResolver
import android.content.Context
import android.net.Uri
import io.mockk.every
import io.mockk.mockk
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import okhttp3.Call
import okhttp3.Callback
import okhttp3.Dispatcher
import okhttp3.EventListener
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.SocketPolicy
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.io.IOException
import java.io.ByteArrayInputStream
import java.nio.file.Files
import java.util.concurrent.CountDownLatch
import java.util.concurrent.atomic.AtomicReference
import java.util.concurrent.TimeUnit

class UploadPartCancellationTest {

    @Test
    fun `part PUT sends exact range headers and bytes and closes its response`() = runBlocking {
        val server = MockWebServer()
        val file = File.createTempFile("upload-part", ".bin")
        val responseClosed = CountDownLatch(1)
        val client = OkHttpClient.Builder().eventListener(object : EventListener() {
            override fun callEnd(call: Call) { responseClosed.countDown() }
        }).build()
        val bytes = ByteArray(20) { it.toByte() }
        file.writeBytes(bytes)
        server.enqueue(MockResponse().setBody("""{"partNumber":4,"size":7}"""))
        server.start()
        try {
            uploadSessionPartHttp(client, server.url("/part").toString(), 4, "upload-token", file, 3, 7, 20)

            val request = server.takeRequest(5, TimeUnit.SECONDS) ?: error("PUT did not reach MockWebServer")
            assertEquals("PUT", request.method)
            assertEquals("application/octet-stream", request.getHeader("Content-Type"))
            assertEquals("bytes 3-9/20", request.getHeader("Content-Range"))
            assertEquals("upload-token", request.getHeader("X-Upload-Token"))
            assertEquals("7", request.getHeader("Content-Length"))
            assertArrayEquals(bytes.copyOfRange(3, 10), request.body.readByteArray())
            assertTrue("response body was not closed", responseClosed.await(5, TimeUnit.SECONDS))
        } finally {
            server.shutdown()
            file.delete()
        }
    }

    @Test
    fun `HTTP failures retain status response body and headers`() = runBlocking {
        val server = MockWebServer()
        val file = File.createTempFile("upload-part-error", ".bin").apply { writeBytes(ByteArray(4)) }
        server.enqueue(MockResponse().setResponseCode(503).addHeader("X-Upload-Error", "storage-busy").setBody("try later"))
        server.start()
        try {
            val error = runCatching {
                uploadSessionPartHttp(OkHttpClient(), server.url("/part").toString(), 1, "token", file, 0, 4, 4)
            }.exceptionOrNull() as UploadPartHttpException

            assertEquals(503, error.code)
            assertEquals("try later", error.body)
            assertEquals("storage-busy", error.headers["X-Upload-Error"]?.single())
            assertTrue(error.retryable)
        } finally {
            server.shutdown()
            file.delete()
        }
    }

    @Test
    fun `cancelling queued PUT finishes without waiting for dispatcher slot`() = runBlocking {
        val server = MockWebServer()
        val file = File.createTempFile("upload-part-queued-cancel", ".bin").apply { writeBytes(ByteArray(4)) }
        val dispatcher = Dispatcher().apply { maxRequestsPerHost = 1 }
        val client = OkHttpClient.Builder().dispatcher(dispatcher).build()
        var upload: kotlinx.coroutines.Job? = null
        var heldCall: Call? = null
        server.enqueue(MockResponse().setSocketPolicy(SocketPolicy.NO_RESPONSE))
        server.start()
        try {
            val activeCall = client.newCall(okhttp3.Request.Builder().url(server.url("/hold")).get().build())
            heldCall = activeCall
            activeCall.enqueue(object : Callback {
                override fun onFailure(call: Call, error: IOException) = Unit
                override fun onResponse(call: Call, response: okhttp3.Response) { response.close() }
            })
            assertTrue("held GET did not reach MockWebServer", server.takeRequest(5, TimeUnit.SECONDS) != null)

            upload = launch(Dispatchers.IO) {
                uploadSessionPartHttp(client, server.url("/part").toString(), 1, "token", file, 0, 4, 4)
            }
            withTimeout(5_000) {
                while (dispatcher.queuedCalls().none { it.request().method == "PUT" }) delay(10)
            }

            withTimeout(1_000) { upload.cancelAndJoin() }
        } finally {
            heldCall?.cancel()
            upload?.cancel()
            server.shutdown()
            upload?.let { withTimeout(5_000) { it.join() } }
            file.delete()
        }
    }

    @Test
    fun `cancelling suspended PUT cancels real OkHttp call`() = runBlocking {
        val server = MockWebServer()
        val file = File.createTempFile("upload-part-cancel", ".bin").apply { writeBytes(ByteArray(64 * 1024)) }
        val callFailed = CountDownLatch(1)
        val client = OkHttpClient.Builder().eventListener(object : EventListener() {
            override fun callFailed(call: Call, ioe: IOException) { callFailed.countDown() }
        }).build()
        server.enqueue(MockResponse().setHeadersDelay(1, TimeUnit.SECONDS).setBody("""{"partNumber":1,"size":65536}"""))
        server.start()
        try {
            val upload = launch(Dispatchers.IO) {
                uploadSessionPartHttp(client, server.url("/part").toString(), 1, "token", file, 0, file.length(), file.length())
            }
            val request = withContext(Dispatchers.IO) { server.takeRequest(5, TimeUnit.SECONDS) }
            assertTrue("PUT did not reach MockWebServer", request != null)

            upload.cancelAndJoin()

            assertTrue("OkHttp did not report the cancelled call", callFailed.await(5, TimeUnit.SECONDS))
        } finally {
            server.shutdown()
            file.delete()
        }
    }

    @Test
    fun `cancelling real PUT closes staged file before avatar cleanup`() = runBlocking {
        val server = MockWebServer()
        val cacheDir = Files.createTempDirectory("avatar-cancel").toFile()
        val context = mockk<Context>()
        val resolver = mockk<ContentResolver>(relaxed = true)
        val uri = mockk<Uri>()
        val writeStarted = CountDownLatch(1)
        val releaseWrite = CountDownLatch(1)
        val callFailed = CountDownLatch(1)
        val stagedFile = AtomicReference<File?>()
        val client = OkHttpClient.Builder().eventListener(object : EventListener() {
            override fun callFailed(call: Call, ioe: IOException) { callFailed.countDown() }
        }).build()
        every { context.cacheDir } returns cacheDir
        every { context.contentResolver } returns resolver
        every { resolver.openInputStream(uri) } returns ByteArrayInputStream(ByteArray(2 * 1024 * 1024) { 3 })
        server.enqueue(MockResponse().setHeadersDelay(1, TimeUnit.SECONDS).setBody("""{"partNumber":1,"size":2097152}"""))
        server.start()
        try {
            val upload = launch(Dispatchers.IO) {
                stageAvatarAndUpload(context, uri) { staged, _, _ ->
                    stagedFile.set(staged)
                    uploadSessionPartHttp(
                        client, server.url("/part").toString(), 1, "token", staged, 0, staged.length(), staged.length(),
                    ) {
                        writeStarted.countDown()
                        releaseWrite.await()
                    }
                    "file-id"
                }
            }
            assertTrue("request body write did not start", writeStarted.await(5, TimeUnit.SECONDS))

            upload.cancel()
            releaseWrite.countDown()
            upload.join()

            assertTrue("OkHttp did not finish the cancelled call", callFailed.await(5, TimeUnit.SECONDS))
            assertTrue("staged file was not deleted", stagedFile.get()?.exists() == false)
            assertTrue("cache directory still contains staged data", cacheDir.listFiles().orEmpty().isEmpty())
        } finally {
            releaseWrite.countDown()
            server.shutdown()
            cacheDir.deleteRecursively()
        }
    }
}
