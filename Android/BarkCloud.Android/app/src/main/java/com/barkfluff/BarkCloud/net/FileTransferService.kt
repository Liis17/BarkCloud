package com.barkfluff.BarkCloud.net

import android.content.Context
import android.net.Uri
import android.os.SystemClock
import android.provider.OpenableColumns
import barkcloud.files.FilesApiOuterClass.CreateUploadSessionRequest
import barkcloud.files.FilesApiOuterClass.GetTempDownloadUrlRequest
import barkcloud.files.FilesApiOuterClass.GetUserStorageInfoRequest
import barkcloud.files.FilesApiOuterClass.UploadFileType
import barkcloud.files.FilesApiOuterClass.UploadSessionIdRequest
import barkcloud.files.FilesApiOuterClass.UploadSessionResponse
import barkcloud.files.FilesApiOuterClass.UploadSessionStatus
import com.barkfluff.BarkCloud.data.upload.UploadErrorCodes
import com.barkfluff.BarkCloud.data.upload.UploadFileHasher
import com.barkfluff.BarkCloud.data.upload.UploadPartMath
import com.barkfluff.BarkCloud.grpc.GrpcEndpoint
import com.barkfluff.BarkCloud.grpc.GrpcManager
import com.barkfluff.BarkCloud.grpc.errorCode
import com.barkfluff.BarkCloud.grpc.grpcFailureOrNull
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.delay
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import okhttp3.Call
import okhttp3.Callback
import okhttp3.MediaType.Companion.toMediaTypeOrNull
import okhttp3.Request
import okhttp3.RequestBody
import okhttp3.Response
import okio.BufferedSink
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.io.RandomAccessFile
import java.util.UUID
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

/**
 * Передача байтов файлов: gRPC `FilesApi` (ссылки/квота/upload-сессии) + HTTP
 * для resumable-частей и скачиваний. Аватар использует отдельную foreground-сессию
 * Upload 2.0 без сохранения в очереди.
 */
class FileTransferService(
    private val appContext: Context,
    private val grpc: GrpcManager,
    private val http: Call.Factory,
    private val monotonicNowMillis: () -> Long = { SystemClock.elapsedRealtime() },
) {

    // MARK: gRPC (FilesApi)

    /** Временные ссылки на оригиналы по file_id (file_id → URL, только непустые). */
    suspend fun tempDownloadUrls(fileIds: List<String>): Map<String, String> {
        if (fileIds.isEmpty()) return emptyMap()
        val resp = grpc.filesStub().getTempDownloadUrl(
            GetTempDownloadUrlRequest.newBuilder().addAllFileIds(fileIds).build()
        )
        return resp.fileUrlsList
            .filter { it.url.isNotEmpty() }
            .associate { it.fileId to it.url }
    }

    /** Информация о хранилище (использовано / лимит, в байтах). */
    suspend fun storageInfo(): StorageInfo {
        val resp = grpc.filesStub().getUserStorageInfo(GetUserStorageInfoRequest.getDefaultInstance())
        return StorageInfo(
            resp.totalUsedStorage,
            resp.storageLimit,
            resp.storageByTypesList.groupBy { it.fileType }.mapValues { (_, items) -> items.sumOf { it.usedStorage } },
        )
    }

    // MARK: Upload 2.0 — control plane

    /** Создать идемпотентную resumable-сессию и зарезервировать квоту. */
    suspend fun createUploadSession(
        idempotencyKey: String,
        fileName: String,
        fileSize: Long,
        contentType: String,
        sha256: String,
        fileType: UploadFileType = UploadFileType.CLOUD_FILE,
    ): UploadSessionInfo {
        val resp = grpc.filesStub().createUploadSession(
            createUploadSessionRequest(idempotencyKey, fileName, fileSize, contentType, sha256, fileType)
        )
        return resp.toInfo()
    }

    /** Состояние сессии без выдачи upload-token. */
    suspend fun getUploadSession(sessionId: String): UploadSessionInfo =
        grpc.filesStub().getUploadSession(UploadSessionIdRequest.newBuilder().setSessionId(sessionId).build()).toInfo()

    /** Новый upload-token и фактический список принятых S3-частей. */
    suspend fun resumeUploadSession(sessionId: String): UploadSessionInfo =
        grpc.filesStub().resumeUploadSession(UploadSessionIdRequest.newBuilder().setSessionId(sessionId).build()).toInfo()

    /** Завершить multipart и поставить файл в фоновую обработку. */
    suspend fun completeUploadSession(sessionId: String): UploadSessionInfo =
        grpc.filesStub().completeUploadSession(UploadSessionIdRequest.newBuilder().setSessionId(sessionId).build()).toInfo()

    /** Отменить незавершённую сессию (S3 abort + освобождение резерва квоты). */
    suspend fun cancelUploadSession(sessionId: String): UploadSessionInfo =
        grpc.filesStub().cancelUploadSession(UploadSessionIdRequest.newBuilder().setSessionId(sessionId).build()).toInfo()

    // MARK: Upload 2.0 — data plane

    fun uploadSessionPartUrl(sessionId: String, partNumber: Int): String =
        "${GrpcEndpoint.fileUploadBase}/file-upload/$sessionId/parts/$partNumber"

    /**
     * Отправить одну часть: `PUT /file-upload/{session}/parts/{n}` с окном
     * `[offset, offset + size)` файла [file]. Авторизация — только `X-Upload-Token`.
     * Успех подтверждается JSON-квитанцией с ожидаемыми `partNumber`/`size`.
     */
    suspend fun uploadSessionPart(
        sessionId: String,
        partNumber: Int,
        token: String,
        file: File,
        offset: Long,
        size: Long,
        totalSize: Long,
        onProgress: (sentInPart: Long) -> Unit = {},
    ): Unit = uploadSessionPartHttp(
        http, uploadSessionPartUrl(sessionId, partNumber), partNumber, token,
        file, offset, size, totalSize, onProgress,
    )

    /** Загружает URI аватара в foreground и возвращает file_id только после READY. */
    suspend fun uploadAvatar(uri: Uri, fileName: String? = null): String =
        stageAvatarAndUpload(appContext, uri, fileName) { file, actualName, mimeType ->
            uploadAvatarFile(file, actualName, mimeType, GrpcAvatarUploadApi())
        }

    /** Скачать оригинал во временный файл (для предпросмотра / шеринга). */
    suspend fun download(urlString: String, suggestedName: String): File =
        withContext(Dispatchers.IO) {
            suspendCancellableCoroutine { continuation ->
                val call = http.newCall(Request.Builder().url(urlString).get().build())
                val destinationLock = Any()
                var completedDestination: File? = null
                var cancelled = false
                continuation.invokeOnCancellation {
                    call.cancel()
                    synchronized(destinationLock) {
                        cancelled = true
                        completedDestination?.delete()
                    }
                }
                call.enqueue(object : Callback {
                    override fun onFailure(call: Call, error: IOException) {
                        if (continuation.isActive) continuation.resumeWithException(error)
                    }

                    override fun onResponse(call: Call, response: Response) {
                        var destination: File? = null
                        try {
                            response.use { resp ->
                                if (!continuation.isActive) return
                                if (!resp.isSuccessful) throw IOException("Download failed: HTTP ${resp.code}")
                                val name = suggestedName.ifEmpty { UUID.randomUUID().toString() }
                                destination = File(appContext.cacheDir, name).apply { delete() }
                                val source = resp.body ?: throw IOException("Download: empty body")
                                source.byteStream().use { input ->
                                    destination!!.outputStream().use { input.copyTo(it) }
                                }
                            }
                            val completed = requireNotNull(destination)
                            synchronized(destinationLock) {
                                if (cancelled || !continuation.isActive) {
                                    completed.delete()
                                } else {
                                    completedDestination = completed
                                    continuation.resume(completed)
                                }
                            }
                        } catch (error: Throwable) {
                            destination?.delete()
                            if (continuation.isActive) continuation.resumeWithException(error)
                        }
                    }
                })
            }
        }

    private fun UploadSessionResponse.toInfo(): UploadSessionInfo = UploadSessionInfo(
        sessionId = sessionId,
        fileId = fileId,
        state = when (status) {
            UploadSessionStatus.UPLOAD_SESSION_STATUS_UPLOADING -> UploadSessionState.UPLOADING
            UploadSessionStatus.UPLOAD_SESSION_STATUS_PROCESSING -> UploadSessionState.PROCESSING
            UploadSessionStatus.UPLOAD_SESSION_STATUS_READY -> UploadSessionState.READY
            UploadSessionStatus.UPLOAD_SESSION_STATUS_FAILED -> UploadSessionState.FAILED
            UploadSessionStatus.UPLOAD_SESSION_STATUS_CANCELLED -> UploadSessionState.CANCELLED
            UploadSessionStatus.UPLOAD_SESSION_STATUS_EXPIRED -> UploadSessionState.EXPIRED
            else -> throw IOException("Unknown upload session status: $status")
        },
        fileSize = fileSize,
        partSize = partSize,
        uploadToken = uploadToken.takeIf { it.isNotEmpty() },
        uploadedParts = uploadedPartsList.map { UploadSessionPart(it.partNumber, it.size, it.hasEtag) },
        errorCode = errorCode.takeIf { it.isNotEmpty() },
        errorMessage = errorMessage.takeIf { it.isNotEmpty() },
    )

    data class StorageInfo(
        val used: Long,
        val limit: Long,
        val byType: Map<UploadFileType, Long> = emptyMap(),
    )

    internal inner class GrpcAvatarUploadApi : AvatarUploadApi {
        override var lastAccessToken: String? = null
            private set

        private suspend fun <T : Any> filesCall(
            timeoutMillis: Long,
            block: suspend (barkcloud.files.FilesApiGrpcKt.FilesApiCoroutineStub) -> T,
        ): T {
            val result = withTimeoutOrNull(timeoutMillis) {
                val startedAt = monotonicNowMillis()
                val token = grpc.validAccessToken()?.takeIf { it.isNotBlank() }
                    ?: throw IOException("Требуется авторизация для загрузки аватара")
                lastAccessToken = token
                val remainingMillis = timeoutMillis - (monotonicNowMillis() - startedAt)
                if (remainingMillis <= 0) null else block(
                    grpc.filesStub(token).withDeadlineAfter(remainingMillis, TimeUnit.MILLISECONDS)
                )
            }
            return result ?: throw IOException("Истекло время ожидания Files API")
        }

        override suspend fun createUploadSession(
            idempotencyKey: String,
            fileName: String,
            fileSize: Long,
            contentType: String,
            sha256: String,
            timeoutMillis: Long,
        ): UploadSessionInfo = filesCall(timeoutMillis) { stub ->
            stub.createUploadSession(
                createUploadSessionRequest(
                    idempotencyKey, fileName, fileSize, contentType, sha256, UploadFileType.USER_AVATAR,
                )
            ).toInfo()
        }

        override suspend fun getUploadSession(sessionId: String, timeoutMillis: Long): UploadSessionInfo =
            filesCall(timeoutMillis) { stub ->
                stub.getUploadSession(UploadSessionIdRequest.newBuilder().setSessionId(sessionId).build()).toInfo()
            }

        override suspend fun resumeUploadSession(sessionId: String, timeoutMillis: Long): UploadSessionInfo =
            filesCall(timeoutMillis) { stub ->
                stub.resumeUploadSession(UploadSessionIdRequest.newBuilder().setSessionId(sessionId).build()).toInfo()
            }

        override suspend fun completeUploadSession(sessionId: String, timeoutMillis: Long): UploadSessionInfo =
            filesCall(timeoutMillis) { stub ->
                stub.completeUploadSession(UploadSessionIdRequest.newBuilder().setSessionId(sessionId).build()).toInfo()
            }

        override suspend fun cancelUploadSession(
            sessionId: String,
            accessToken: String,
            timeoutMillis: Long,
        ) {
            withContext(NonCancellable) {
                withTimeoutOrNull(timeoutMillis) {
                    grpc.filesStub(accessToken).withDeadlineAfter(timeoutMillis, TimeUnit.MILLISECONDS)
                        .cancelUploadSession(UploadSessionIdRequest.newBuilder().setSessionId(sessionId).build())
                }
            }
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
            this@FileTransferService.uploadSessionPart(
                sessionId, partNumber, token, file, offset, size, totalSize,
            )
        }
    }

}

internal suspend fun stageAvatarAndUpload(
    context: Context,
    uri: Uri,
    fallbackFileName: String? = null,
    upload: suspend (File, String, String) -> String,
): String {
    var staged: File? = null
    try {
        val file = withContext(Dispatchers.IO) {
            val target = File.createTempFile("avatar-", ".upload", context.cacheDir)
            staged = target
            try {
                val input = context.contentResolver.openInputStream(uri)
                    ?: throw IOException("Не удалось открыть изображение аватара")
                input.use { source -> target.outputStream().use(source::copyTo) }
                target
            } catch (error: Throwable) {
                target.delete()
                throw error
            }
        }
        val metadata = withContext(Dispatchers.IO) {
            val queriedName = runCatching { queryAvatarFileName(context, uri) }.getOrNull()
            val mimeType = runCatching { context.contentResolver.getType(uri) }
                .getOrNull()?.takeIf { it.isNotBlank() } ?: "application/octet-stream"
            (queriedName?.takeIf { it.isNotBlank() }
                ?: fallbackFileName?.takeIf { it.isNotBlank() }
                ?: "avatar") to mimeType
        }
        return upload(file, metadata.first, metadata.second)
    } finally {
        staged?.delete()
    }
}

private fun queryAvatarFileName(context: Context, uri: Uri): String? =
    context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { cursor ->
        val index = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
        if (cursor.moveToFirst() && index >= 0 && !cursor.isNull(index)) cursor.getString(index) else null
    }

internal suspend fun uploadSessionPartHttp(
    http: Call.Factory,
    url: String,
    partNumber: Int,
    token: String,
    file: File,
    offset: Long,
    size: Long,
    totalSize: Long,
    onProgress: (sentInPart: Long) -> Unit = {},
): Unit = withContext(Dispatchers.IO) {
    val body = FileSegmentRequestBody(file, offset, size, onProgress)
    val request = Request.Builder()
        .url(url)
        .put(body)
        .header("Content-Type", "application/octet-stream")
        .header("Content-Range", "bytes ${offset}-${offset + size - 1}/$totalSize")
        .header("X-Upload-Token", token)
        .build()

    try {
        suspendCancellableCoroutine { continuation ->
            val call = http.newCall(request)
            continuation.invokeOnCancellation {
                // A queued call may reach writeTo later, so close the body gate before cancelling it.
                body.cancel()
                call.cancel()
            }
            try {
                call.enqueue(object : Callback {
                    override fun onFailure(call: Call, error: IOException) {
                        continuation.resumeWithException(error)
                    }

                    override fun onResponse(call: Call, response: Response) {
                        val result = runCatching {
                            response.use { resp ->
                                val text = resp.body?.string().orEmpty()
                                val headers = resp.headers.toMultimap()
                                if (!resp.isSuccessful) throw UploadPartHttpException(resp.code, text, headers)
                                val ack = PartAckParser.parse(text)
                                    ?: throw UploadPartHttpException(resp.code, text, headers)
                                if (ack.first != partNumber || ack.second != size) {
                                    throw UploadPartHttpException(resp.code, text, headers)
                                }
                            }
                        }
                        result.fold(
                            onSuccess = { continuation.resume(Unit) },
                            onFailure = continuation::resumeWithException,
                        )
                    }
                })
            } catch (error: Throwable) {
                continuation.resumeWithException(error)
            }
        }
    } catch (cancelled: CancellationException) {
        withContext(NonCancellable) { body.cancelAndAwaitWrites() }
        throw cancelled
    }
}

/** Состояние upload-сессии на сервере. */
enum class UploadSessionState { UPLOADING, PROCESSING, READY, FAILED, CANCELLED, EXPIRED }

data class UploadSessionPart(val partNumber: Int, val size: Long, val hasEtag: Boolean)

data class UploadSessionInfo(
    val sessionId: String,
    val fileId: String,
    val state: UploadSessionState,
    val fileSize: Long,
    val partSize: Long,
    val uploadToken: String?,
    val uploadedParts: List<UploadSessionPart>,
    val errorCode: String?,
    val errorMessage: String?,
)

internal fun createUploadSessionRequest(
    idempotencyKey: String,
    fileName: String,
    fileSize: Long,
    contentType: String,
    sha256: String,
    fileType: UploadFileType = UploadFileType.CLOUD_FILE,
): CreateUploadSessionRequest = CreateUploadSessionRequest.newBuilder()
    .setIdempotencyKey(idempotencyKey)
    .setFileName(fileName)
    .setFileSize(fileSize)
    .setContentType(contentType)
    .setSha256(sha256)
    .setFileType(fileType)
    .build()

internal interface AvatarUploadApi {
    val lastAccessToken: String?

    suspend fun createUploadSession(
        idempotencyKey: String,
        fileName: String,
        fileSize: Long,
        contentType: String,
        sha256: String,
        timeoutMillis: Long,
    ): UploadSessionInfo

    suspend fun getUploadSession(sessionId: String, timeoutMillis: Long): UploadSessionInfo
    suspend fun resumeUploadSession(sessionId: String, timeoutMillis: Long): UploadSessionInfo
    suspend fun completeUploadSession(sessionId: String, timeoutMillis: Long): UploadSessionInfo
    suspend fun cancelUploadSession(sessionId: String, accessToken: String, timeoutMillis: Long)
    suspend fun uploadPart(
        sessionId: String,
        partNumber: Int,
        token: String,
        file: File,
        offset: Long,
        size: Long,
        totalSize: Long,
    )
}

internal suspend fun uploadAvatarFile(
    file: File,
    fileName: String,
    contentType: String,
    api: AvatarUploadApi,
    nowMillis: () -> Long = { SystemClock.elapsedRealtime() },
    wait: suspend (Long) -> Unit = { delay(it) },
): String {
    val (sha256, fileSize) = withContext(Dispatchers.IO) { UploadFileHasher.hashAndSize(file) }
        ?: throw IOException("Не удалось прочитать изображение аватара")
    if (fileSize == 0L) throw IOException("Файл аватара пустой")
    if (fileSize > MAX_AVATAR_BYTES) throw IOException("Размер аватара превышает 20 MiB")

    val idempotencyKey = UUID.randomUUID().toString()
    var sessionId: String? = null
    var shouldCancelSession = false

    suspend fun sendMissingParts(initial: UploadSessionInfo): UploadSessionInfo {
        var current = initial
        if (current.partSize <= 0L) throw IOException("Сервер вернул некорректный размер части")
        if (current.fileSize != fileSize) throw IOException("Сервер вернул другой размер файла")

        var token = current.uploadToken
        if (token.isNullOrBlank()) {
            current = api.resumeUploadSession(current.sessionId, AVATAR_RPC_TIMEOUT_MILLIS)
            shouldCancelSession = current.state == UploadSessionState.UPLOADING
            if (current.state != UploadSessionState.UPLOADING) return current
            token = current.uploadToken?.takeIf { it.isNotBlank() }
                ?: throw IOException("Сервер не выдал upload-token")
        }

        var resumedAfterUnauthorized = false
        while (true) {
            if (current.state != UploadSessionState.UPLOADING) return current
            if (current.fileSize != fileSize || current.partSize <= 0L) {
                throw IOException("Сервер вернул некорректные параметры upload-сессии")
            }
            var restart = false
            for (partNumber in UploadPartMath.missingPartNumbers(current.uploadedParts, fileSize, current.partSize)) {
                val offset = UploadPartMath.partOffset(partNumber, current.partSize)
                val size = UploadPartMath.partLength(partNumber, fileSize, current.partSize)
                try {
                    uploadPartWithRetry(
                        api, current.sessionId, partNumber,
                        token ?: throw IOException("Сервер не выдал upload-token"),
                        file, offset, size, fileSize, wait,
                    )
                } catch (error: UploadPartHttpException) {
                    if (error.code != 401 || resumedAfterUnauthorized) throw error
                    resumedAfterUnauthorized = true
                    current = api.resumeUploadSession(current.sessionId, AVATAR_RPC_TIMEOUT_MILLIS)
                    shouldCancelSession = current.state == UploadSessionState.UPLOADING
                    if (current.state != UploadSessionState.UPLOADING) return current
                    token = current.uploadToken?.takeIf { it.isNotBlank() }
                        ?: throw IOException("Сервер не выдал upload-token после resume")
                    restart = true
                    break
                }
                current = current.copy(
                    uploadedParts = current.uploadedParts.filterNot { it.partNumber == partNumber } +
                        UploadSessionPart(partNumber, size, hasEtag = true),
                    uploadToken = token,
                )
            }
            if (!restart) return current
        }
    }

    suspend fun completeAndReconcile(current: UploadSessionInfo, recoveryUsed: Boolean): UploadSessionInfo {
        try {
            return api.completeUploadSession(current.sessionId, AVATAR_RPC_TIMEOUT_MILLIS)
        } catch (error: CancellationException) {
            throw error
        } catch (error: Throwable) {
            val reconciled = try {
                api.getUploadSession(current.sessionId, AVATAR_RPC_TIMEOUT_MILLIS)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Throwable) {
                throw error
            }
            if (reconciled.state != UploadSessionState.UPLOADING) return reconciled
            val incomplete = error.grpcFailureOrNull()?.errorCode() == UploadErrorCodes.UPLOAD_PARTS_INCOMPLETE
            if (!incomplete || recoveryUsed) throw error

            val resumed = api.resumeUploadSession(current.sessionId, AVATAR_RPC_TIMEOUT_MILLIS)
            shouldCancelSession = resumed.state == UploadSessionState.UPLOADING
            if (resumed.state != UploadSessionState.UPLOADING) return resumed
            val repaired = sendMissingParts(resumed)
            shouldCancelSession = repaired.state == UploadSessionState.UPLOADING
            if (repaired.state != UploadSessionState.UPLOADING) return repaired
            return completeAndReconcile(repaired, recoveryUsed = true)
        }
    }

    suspend fun waitForReady(initial: UploadSessionInfo, startedAt: Long): UploadSessionInfo {
        var current = initial
        while (current.state == UploadSessionState.PROCESSING) {
            val remaining = AVATAR_PROCESSING_TIMEOUT_MILLIS - (nowMillis() - startedAt)
            if (remaining <= 0L) throw IOException("Сервер не обработал аватар за 120 секунд")
            wait(minOf(AVATAR_POLL_MILLIS, remaining))
            val rpcRemaining = AVATAR_PROCESSING_TIMEOUT_MILLIS - (nowMillis() - startedAt)
            if (rpcRemaining <= 0L) throw IOException("Сервер не обработал аватар за 120 секунд")
            current = api.getUploadSession(
                current.sessionId,
                minOf(AVATAR_RPC_TIMEOUT_MILLIS, rpcRemaining),
            )
        }
        return current
    }

    try {
        var session = api.createUploadSession(
            idempotencyKey, fileName, fileSize, contentType, sha256, AVATAR_RPC_TIMEOUT_MILLIS,
        )
        sessionId = session.sessionId.takeIf { it.isNotBlank() }
            ?: throw IOException("Сервер не вернул идентификатор upload-сессии")
        shouldCancelSession = session.state == UploadSessionState.UPLOADING
        if (session.fileSize != fileSize) throw IOException("Сервер вернул другой размер файла")

        var processingStartedAt: Long? = if (session.state == UploadSessionState.PROCESSING) nowMillis() else null
        while (true) {
            when (session.state) {
                UploadSessionState.READY -> return session.fileId.takeIf { it.isNotBlank() }
                    ?: throw IOException("Сервер вернул READY без file_id")
                UploadSessionState.PROCESSING -> {
                    shouldCancelSession = false
                    session = waitForReady(session, processingStartedAt ?: nowMillis())
                    processingStartedAt = null
                }
                UploadSessionState.UPLOADING -> {
                    shouldCancelSession = true
                    session = sendMissingParts(session)
                    shouldCancelSession = session.state == UploadSessionState.UPLOADING
                    if (session.state != UploadSessionState.UPLOADING) continue

                    session = completeAndReconcile(session, recoveryUsed = false)
                    shouldCancelSession = session.state == UploadSessionState.UPLOADING
                    if (session.state == UploadSessionState.PROCESSING) processingStartedAt = nowMillis()
                    if (session.state == UploadSessionState.UPLOADING) {
                        throw IOException("Сервер не завершил upload-сессию")
                    }
                }
                UploadSessionState.FAILED,
                UploadSessionState.CANCELLED,
                UploadSessionState.EXPIRED,
                -> throw IOException(session.errorMessage ?: "Upload-сессия аватара ${session.state}")
            }
        }
    } finally {
        val activeSessionId = sessionId
        val token = api.lastAccessToken
        if (shouldCancelSession && activeSessionId != null && token != null) {
            runCatching {
                withContext(NonCancellable) {
                    withTimeoutOrNull(AVATAR_CLEANUP_TIMEOUT_MILLIS) {
                        api.cancelUploadSession(activeSessionId, token, AVATAR_CLEANUP_TIMEOUT_MILLIS)
                    }
                }
            }
        }
    }
}

private suspend fun uploadPartWithRetry(
    api: AvatarUploadApi,
    sessionId: String,
    partNumber: Int,
    token: String,
    file: File,
    offset: Long,
    size: Long,
    totalSize: Long,
    wait: suspend (Long) -> Unit,
) {
    for (attempt in 0 until AVATAR_PART_ATTEMPTS) {
        try {
            api.uploadPart(sessionId, partNumber, token, file, offset, size, totalSize)
            return
        } catch (error: CancellationException) {
            throw error
        } catch (error: UploadPartHttpException) {
            if (error.code == 401 || !error.retryable || attempt == AVATAR_PART_ATTEMPTS - 1) throw error
        } catch (error: IOException) {
            if (attempt == AVATAR_PART_ATTEMPTS - 1) throw error
        }
        wait(AVATAR_PART_RETRY_BASE_MILLIS shl attempt)
    }
}

private const val MAX_AVATAR_BYTES = 20L * 1024 * 1024
private const val AVATAR_RPC_TIMEOUT_MILLIS = 15_000L
private const val AVATAR_CLEANUP_TIMEOUT_MILLIS = 5_000L
private const val AVATAR_PROCESSING_TIMEOUT_MILLIS = 120_000L
private const val AVATAR_POLL_MILLIS = 2_000L
private const val AVATAR_PART_ATTEMPTS = 3
private const val AVATAR_PART_RETRY_BASE_MILLIS = 500L

/** Ошибка PUT части: HTTP-код + тело ответа сервера. */
class UploadPartHttpException(
    val code: Int,
    val body: String,
    val headers: Map<String, List<String>> = emptyMap(),
) : IOException("HTTP $code: $body") {
    val retryable: Boolean get() = code == 401 || code == 408 || code == 429 || code in 500..599
}

/** Парсер JSON-квитанции части `{partNumber, size}`; null, если ответ не является квитанцией. */
internal object PartAckParser {
    fun parse(body: String): Pair<Int, Long>? {
        val obj = runCatching { JSONObject(body) }.getOrNull() ?: return null
        val partNumber = obj.optInt("partNumber", -1)
        val size = obj.optLong("size", -1L)
        return if (partNumber > 0 && size >= 0) partNumber to size else null
    }
}

/** Тело запроса = окно `[offset, offset + length)` файла, стримится без буферизации в RAM. */
internal class FileSegmentRequestBody(
    private val file: File,
    private val offset: Long,
    private val length: Long,
    private val onProgress: (sent: Long) -> Unit,
) : RequestBody() {

    private val lifecycleLock = Any()
    private val writesFinished = CompletableDeferred<Unit>()
    private var cancelled = false
    private var activeWriters = 0

    override fun contentType() = OCTET_STREAM
    override fun contentLength(): Long = length

    override fun writeTo(sink: BufferedSink) {
        val raf = synchronized(lifecycleLock) {
            if (cancelled) throw IOException("Upload part body was cancelled")
            val opened = RandomAccessFile(file, "r")
            try {
                opened.seek(offset)
            } catch (error: Throwable) {
                runCatching { opened.close() }
                throw error
            }
            activeWriters++
            opened
        }
        try {
            raf.use { activeFile ->
                val buffer = ByteArray(64 * 1024)
                var sent = 0L
                while (sent < length) {
                    val toRead = minOf(buffer.size.toLong(), length - sent).toInt()
                    val read = activeFile.read(buffer, 0, toRead)
                    if (read < 0) throw IOException("Unexpected EOF in ${file.path} at ${offset + sent}")
                    sink.write(buffer, 0, read)
                    sent += read
                    onProgress(sent)
                }
            }
        } finally {
            synchronized(lifecycleLock) {
                activeWriters--
                if (cancelled && activeWriters == 0) writesFinished.complete(Unit)
            }
        }
    }

    fun cancel() {
        synchronized(lifecycleLock) {
            cancelled = true
            if (activeWriters == 0) writesFinished.complete(Unit)
        }
    }

    suspend fun cancelAndAwaitWrites() {
        cancel()
        writesFinished.await()
    }

    private companion object {
        val OCTET_STREAM = "application/octet-stream".toMediaTypeOrNull()
    }
}
