package com.barkfluff.BarkCloud.net

import android.content.ContentResolver
import android.content.Context
import android.database.Cursor
import android.net.Uri
import android.provider.OpenableColumns
import barkcloud.files.FilesApiOuterClass.UploadFileType
import com.barkfluff.BarkCloud.data.upload.UploadErrorCodes
import io.grpc.Metadata
import io.grpc.Status
import io.grpc.StatusRuntimeException
import io.mockk.every
import io.mockk.mockk
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.io.IOException
import java.io.RandomAccessFile
import java.nio.file.Files

class AvatarUploadFlowTest {

    @Test
    fun `upload session request declares avatar type and cloud default`() {
        val avatar = createUploadSessionRequest("key", "photo.png", 5, "image/png", "hash", UploadFileType.USER_AVATAR)
        val cloud = createUploadSessionRequest("key", "file.bin", 5, "application/octet-stream", "hash")

        assertTrue(avatar.hasFileType())
        assertEquals(UploadFileType.USER_AVATAR, avatar.fileType)
        assertTrue(cloud.hasFileType())
        assertEquals(UploadFileType.CLOUD_FILE, cloud.fileType)
    }

    @Test
    fun `avatar sends only parts missing an ETag or with the wrong size then waits for ready`() = runTest {
        val bytes = ByteArray(25) { it.toByte() }
        val file = tempFile(bytes)
        val api = FakeAvatarUploadApi(
            created = avatarSession(
                bytes.size.toLong(),
                parts = listOf(UploadSessionPart(1, 10, false), UploadSessionPart(2, 9, true)),
            ),
        )
        api.getHandler = { count -> if (count == 1) avatarSession(25, UploadSessionState.PROCESSING) else avatarSession(25, UploadSessionState.READY) }
        var now = 0L
        val waits = mutableListOf<Long>()
        try {
            val fileId = uploadAvatarFile(
                file, "real-name.webp", "image/webp", api,
                nowMillis = { now },
                wait = { waits += it; now += it },
            )

            assertEquals("file-id", fileId)
            assertEquals(listOf(1, 2, 3), api.parts.map { it.number })
            assertEquals(listOf(10L, 10L, 5L), api.parts.map { it.size })
            assertEquals(bytes.toList(), api.parts.flatMap { it.bytes.toList() })
            assertEquals(listOf(2_000L, 2_000L), waits)
            assertTrue(api.operations.indexOf("complete") < api.operations.indexOf("get-1"))
            assertEquals("real-name.webp", api.createdFileName)
            assertEquals("image/webp", api.createdContentType)
            assertTrue(api.createdSha256?.matches(Regex("[0-9a-f]{64}")) == true)
        } finally {
            file.delete()
        }
    }

    @Test
    fun `ready create response returns file id without uploading`() = runTest {
        val file = tempFile(byteArrayOf(1, 2, 3))
        val api = FakeAvatarUploadApi(created = avatarSession(3, UploadSessionState.READY))
        try {
            assertEquals("file-id", uploadAvatarFile(file, "photo.png", "image/png", api))
            assertTrue(api.parts.isEmpty())
            assertEquals(0, api.completeCalls)
        } finally {
            file.delete()
        }
    }

    @Test
    fun `401 resumes once and skips the part reported accepted by server`() = runTest {
        val file = tempFile(ByteArray(25) { it.toByte() })
        val api = FakeAvatarUploadApi(created = avatarSession(25))
        api.partHandler = { part, call ->
            if (part.number == 1 && call == 1) throw UploadPartHttpException(401, "expired")
        }
        api.resumeHandler = {
            avatarSession(25, token = "fresh", parts = listOf(UploadSessionPart(1, 10, true)))
        }
        api.getHandler = { avatarSession(25, UploadSessionState.READY) }
        try {
            uploadAvatarFile(file, "photo.png", "image/png", api, nowMillis = { 0L })

            assertEquals(listOf(1, 2, 3), api.parts.map { it.number })
            assertEquals(listOf("old-token", "fresh", "fresh"), api.parts.map { it.token })
            assertEquals(1, api.resumeCalls)
        } finally {
            file.delete()
        }
    }

    @Test
    fun `transient PUT errors retry three times with increasing waits`() = runTest {
        val file = tempFile(byteArrayOf(1, 2, 3))
        val api = FakeAvatarUploadApi(created = avatarSession(3, partSize = 3))
        api.partHandler = { _, call -> if (call < 3) throw UploadPartHttpException(503, "retry") }
        api.getHandler = { avatarSession(3, UploadSessionState.READY) }
        val waits = mutableListOf<Long>()
        try {
            uploadAvatarFile(file, "photo.png", "image/png", api, nowMillis = { 0L }, wait = { waits += it })
            assertEquals(3, api.parts.size)
            assertEquals(listOf(500L, 1_000L, 2_000L), waits)
        } finally {
            file.delete()
        }
    }

    @Test
    fun `lost complete response reconciles processing without resending parts`() = runTest {
        val file = tempFile(byteArrayOf(1, 2, 3))
        val api = FakeAvatarUploadApi(created = avatarSession(3, partSize = 3))
        api.completeHandler = { throw IOException("connection lost") }
        api.getHandler = { count -> if (count == 1) avatarSession(3, UploadSessionState.PROCESSING) else avatarSession(3, UploadSessionState.READY) }
        try {
            assertEquals("file-id", uploadAvatarFile(file, "photo.png", "image/png", api, nowMillis = { 0L }))
            assertEquals(1, api.completeCalls)
            assertTrue(api.parts.isNotEmpty())
            assertEquals(0, api.resumeCalls)
        } finally {
            file.delete()
        }
    }

    @Test
    fun `incomplete complete gets one resume missing-part repair and one retry`() = runTest {
        val file = tempFile(ByteArray(25) { it.toByte() })
        val api = FakeAvatarUploadApi(created = avatarSession(25))
        api.completeHandler = { count ->
            if (count == 1) throw avatarGrpcError(UploadErrorCodes.UPLOAD_PARTS_INCOMPLETE)
            avatarSession(25, UploadSessionState.PROCESSING)
        }
        api.getHandler = { count ->
            if (count == 1) avatarSession(25, parts = listOf(UploadSessionPart(1, 10, true), UploadSessionPart(3, 5, true)))
            else avatarSession(25, UploadSessionState.READY)
        }
        api.resumeHandler = {
            avatarSession(25, token = "resumed", parts = listOf(UploadSessionPart(1, 10, true), UploadSessionPart(3, 5, true)))
        }
        try {
            uploadAvatarFile(file, "photo.png", "image/png", api, nowMillis = { 0L })
            assertEquals(2, api.completeCalls)
            assertEquals(1, api.resumeCalls)
            assertEquals(listOf(1, 2, 3, 2), api.parts.map { it.number })
            assertEquals("resumed", api.parts.last().token)
        } finally {
            file.delete()
        }
    }

    @Test
    fun `terminal failure and empty ready id are rejected`() = runTest {
        val file = tempFile(byteArrayOf(1, 2, 3))
        try {
            val failed = FakeAvatarUploadApi(created = avatarSession(3, UploadSessionState.FAILED, error = "processing failed"))
            val failure = runCatching { uploadAvatarFile(file, "photo.png", "image/png", failed) }.exceptionOrNull()
            assertEquals("processing failed", failure?.message)
            assertTrue(failed.parts.isEmpty())

            val readyWithoutId = FakeAvatarUploadApi(created = avatarSession(3, UploadSessionState.READY, fileId = ""))
            val missingId = runCatching { uploadAvatarFile(file, "photo.png", "image/png", readyWithoutId) }.exceptionOrNull()
            assertTrue(missingId is IOException)
            assertEquals(0, readyWithoutId.completeCalls)
        } finally {
            file.delete()
        }
    }

    @Test
    fun `empty and oversized files are rejected before session creation`() = runTest {
        val empty = tempFile(byteArrayOf())
        val oversized = File.createTempFile("avatar-large", ".bin").also {
            RandomAccessFile(it, "rw").use { file -> file.setLength(20L * 1024 * 1024 + 1) }
        }
        val emptyApi = FakeAvatarUploadApi(created = avatarSession(1))
        val largeApi = FakeAvatarUploadApi(created = avatarSession(1))
        try {
            assertTrue(runCatching { uploadAvatarFile(empty, "empty", "image/png", emptyApi) }.exceptionOrNull() is IOException)
            assertTrue(runCatching { uploadAvatarFile(oversized, "large", "image/png", largeApi) }.exceptionOrNull() is IOException)
            assertEquals(0, emptyApi.createCalls)
            assertEquals(0, largeApi.createCalls)
        } finally {
            empty.delete()
            oversized.delete()
        }
    }

    @Test
    fun `processing timeout is an IOException after 120 seconds`() = runTest {
        val file = tempFile(byteArrayOf(1, 2, 3))
        val api = FakeAvatarUploadApi(created = avatarSession(3, UploadSessionState.PROCESSING))
        var now = 0L
        var waited = 0L
        api.getHandler = { avatarSession(3, UploadSessionState.PROCESSING) }
        try {
            val error = runCatching {
                uploadAvatarFile(file, "photo.png", "image/png", api,
                    nowMillis = { now }, wait = { waited += it; now += it })
            }.exceptionOrNull()
            assertTrue(error is IOException)
            assertTrue(error?.message?.contains("120 секунд") == true)
            assertEquals(120_000L, waited)
            assertNull(api.cancelledWithToken)
        } finally {
            file.delete()
        }
    }

    @Test
    fun `cancellation during PUT attempts cleanup with last known token and preserves cancellation`() = runTest {
        val file = tempFile(byteArrayOf(1, 2, 3))
        val api = FakeAvatarUploadApi(created = avatarSession(3, partSize = 3))
        api.partHandler = { _, _ -> awaitCancellation() }
        api.cancelHandler = { throw IOException("session already processing") }
        try {
            val upload = async { uploadAvatarFile(file, "photo.png", "image/png", api) }
            while (api.parts.isEmpty()) kotlinx.coroutines.yield()
            upload.cancelAndJoin()
            assertTrue(upload.isCancelled)
            assertEquals("old-token", api.cancelledWithToken)
        } finally {
            file.delete()
        }
    }

    @Test
    fun `staging uses provider name and mime type and removes cache file after failure`() = runTest {
        val context = mockk<Context>()
        val resolver = mockk<ContentResolver>(relaxed = true)
        val cursor = mockk<Cursor>(relaxed = true)
        val uri = mockk<Uri>()
        val cacheDir = Files.createTempDirectory("avatar-stage").toFile()
        every { context.cacheDir } returns cacheDir
        every { context.contentResolver } returns resolver
        every { resolver.openInputStream(uri) } returns java.io.ByteArrayInputStream(byteArrayOf(7, 8, 9))
        every { resolver.getType(uri) } returns "image/webp"
        every { resolver.query(uri, any(), any(), any(), any()) } returns cursor
        every { cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME) } returns 0
        every { cursor.moveToFirst() } returns true
        every { cursor.isNull(0) } returns false
        every { cursor.getString(0) } returns "provider-name.webp"
        try {
            val failure = runCatching {
                stageAvatarAndUpload(context, uri, "ignored.jpg") { staged, name, mime ->
                    assertTrue(staged.exists())
                    assertEquals("provider-name.webp", name)
                    assertEquals("image/webp", mime)
                    assertEquals(3L, staged.length())
                    throw IOException("profile update failed")
                }
            }.exceptionOrNull()
            assertEquals("profile update failed", failure?.message)
            assertTrue(cacheDir.listFiles().orEmpty().isEmpty())
        } finally {
            cacheDir.deleteRecursively()
        }
    }

    private fun tempFile(bytes: ByteArray): File = File.createTempFile("avatar-flow", ".bin").apply { writeBytes(bytes) }

    private class FakeAvatarUploadApi(var created: UploadSessionInfo) : AvatarUploadApi {
        override var lastAccessToken: String? = "old-token"
        var createCalls = 0
        var resumeCalls = 0
        var completeCalls = 0
        var getCalls = 0
        var createdFileName: String? = null
        var createdContentType: String? = null
        var createdSha256: String? = null
        var cancelledWithToken: String? = null
        val operations = mutableListOf<String>()
        val parts = mutableListOf<PartRequest>()
        var getHandler: suspend (Int) -> UploadSessionInfo = { avatarSession(1, UploadSessionState.READY) }
        var resumeHandler: suspend (Int) -> UploadSessionInfo = { created.copy(uploadToken = "resumed") }
        var completeHandler: suspend (Int) -> UploadSessionInfo = { avatarSession(created.fileSize, UploadSessionState.PROCESSING) }
        var partHandler: suspend (PartRequest, Int) -> Unit = { _, _ -> }
        var cancelHandler: suspend (String) -> Unit = {}

        override suspend fun createUploadSession(
            idempotencyKey: String,
            fileName: String,
            fileSize: Long,
            contentType: String,
            sha256: String,
            timeoutMillis: Long,
        ): UploadSessionInfo {
            createCalls++
            createdFileName = fileName
            createdContentType = contentType
            createdSha256 = sha256
            operations += "create"
            return created
        }

        override suspend fun getUploadSession(sessionId: String, timeoutMillis: Long): UploadSessionInfo {
            getCalls++
            operations += "get-$getCalls"
            return getHandler(getCalls)
        }

        override suspend fun resumeUploadSession(sessionId: String, timeoutMillis: Long): UploadSessionInfo {
            resumeCalls++
            operations += "resume"
            return resumeHandler(resumeCalls)
        }

        override suspend fun completeUploadSession(sessionId: String, timeoutMillis: Long): UploadSessionInfo {
            completeCalls++
            operations += "complete"
            return completeHandler(completeCalls)
        }

        override suspend fun cancelUploadSession(sessionId: String, accessToken: String, timeoutMillis: Long) {
            cancelledWithToken = accessToken
            cancelHandler(accessToken)
        }

        override suspend fun uploadPart(
            sessionId: String,
            partNumber: Int,
            token: String,
            file: File,
            offset: Long,
            size: Long,
            totalSize: Long,
        ) {
            val bytes = RandomAccessFile(file, "r").use { raf ->
                raf.seek(offset)
                ByteArray(size.toInt()).also(raf::readFully)
            }
            val request = PartRequest(partNumber, token, offset, size, totalSize, bytes)
            parts += request
            operations += "part-$partNumber"
            partHandler(request, parts.count { it.number == partNumber })
        }
    }

    private data class PartRequest(
        val number: Int,
        val token: String,
        val offset: Long,
        val size: Long,
        val totalSize: Long,
        val bytes: ByteArray,
    )
}

private fun avatarSession(
    fileSize: Long,
    state: UploadSessionState = UploadSessionState.UPLOADING,
    partSize: Long = 10,
    token: String? = "old-token",
    parts: List<UploadSessionPart> = emptyList(),
    fileId: String = "file-id",
    error: String? = null,
) = UploadSessionInfo("session-id", fileId, state, fileSize, partSize, token, parts, null, error)

private fun avatarGrpcError(code: String): StatusRuntimeException {
    val trailers = Metadata().apply {
        put(Metadata.Key.of("x-error-code", Metadata.ASCII_STRING_MARSHALLER), code)
    }
    return Status.FAILED_PRECONDITION.withDescription("incomplete").asRuntimeException(trailers)
}
