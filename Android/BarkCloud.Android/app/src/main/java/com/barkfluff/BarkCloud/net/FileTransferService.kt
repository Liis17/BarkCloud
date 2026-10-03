package com.barkfluff.BarkCloud.net

import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
import barkcloud.files.FilesApiOuterClass.CreateUploadSessionRequest
import barkcloud.files.FilesApiOuterClass.GetTempDownloadUrlRequest
import barkcloud.files.FilesApiOuterClass.GetUploadUrlRequest
import barkcloud.files.FilesApiOuterClass.GetUserStorageInfoRequest
import barkcloud.files.FilesApiOuterClass.UploadFileType
import barkcloud.files.FilesApiOuterClass.UploadSessionIdRequest
import barkcloud.files.FilesApiOuterClass.UploadSessionResponse
import barkcloud.files.FilesApiOuterClass.UploadSessionStatus
import com.barkfluff.BarkCloud.data.GlobalParam
import com.barkfluff.BarkCloud.grpc.GrpcEndpoint
import com.barkfluff.BarkCloud.grpc.GrpcManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaTypeOrNull
import okhttp3.MultipartBody
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody
import okhttp3.RequestBody.Companion.toRequestBody
import okio.BufferedSink
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.io.RandomAccessFile
import java.util.UUID

/**
 * Передача байтов файлов: gRPC `FilesApi` (ссылки/квота/upload-сессии) + обычный HTTP
 * на готовые URL, которые возвращает сервер. Основной путь загрузки — возобновляемые
 * сессии Upload 2.0 ([createUploadSession] + [uploadSessionPart]); legacy multipart
 * `/upload/{id}` остаётся только для аватара.
 */
class FileTransferService(
    private val appContext: Context,
    private val grpc: GrpcManager,
    private val globalParam: GlobalParam,
    private val http: OkHttpClient,
) {

    // MARK: gRPC (FilesApi)

    /** Получить адрес для загрузки и предварительный file_id. */
    suspend fun getUploadUrl(type: UploadFileType): UploadTarget {
        val resp = grpc.filesStub().getUploadUrl(
            GetUploadUrlRequest.newBuilder().setFileType(type).build()
        )
        return UploadTarget(resp.url, resp.fileId)
    }

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
        return StorageInfo(resp.totalUsedStorage, resp.storageLimit)
    }

    // MARK: Upload 2.0 — control plane

    /** Создать идемпотентную resumable-сессию и зарезервировать квоту. */
    suspend fun createUploadSession(
        idempotencyKey: String,
        fileName: String,
        fileSize: Long,
        contentType: String,
        sha256: String,
    ): UploadSessionInfo {
        val resp = grpc.filesStub().createUploadSession(
            CreateUploadSessionRequest.newBuilder()
                .setIdempotencyKey(idempotencyKey)
                .setFileName(fileName)
                .setFileSize(fileSize)
                .setContentType(contentType)
                .setSha256(sha256)
                .build()
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
    ): Unit = withContext(Dispatchers.IO) {
        val body = FileSegmentRequestBody(file, offset, size, onProgress)
        val request = Request.Builder()
            .url(uploadSessionPartUrl(sessionId, partNumber))
            .put(body)
            .header("Content-Type", "application/octet-stream")
            .header("Content-Range", "bytes ${offset}-${offset + size - 1}/$totalSize")
            .header("X-Upload-Token", token)
            .build()
        http.newCall(request).execute().use { resp ->
            val text = resp.body?.string().orEmpty()
            if (!resp.isSuccessful) throw UploadPartHttpException(resp.code, text)
            val ack = PartAckParser.parse(text)
                ?: throw UploadPartHttpException(resp.code, text)
            if (ack.first != partNumber || ack.second != size) {
                throw UploadPartHttpException(resp.code, text)
            }
        }
    }

    // MARK: Legacy HTTP (аватар)

    /** Залить содержимое [uri] (стримингом). Возвращает file_id ИЗ ОТВЕТА. */
    suspend fun upload(
        uri: Uri,
        fileName: String,
        urlString: String,
    ): String = upload(uriRequestBody(uri), fileName, urlString)

    private suspend fun upload(body: RequestBody, fileName: String, urlString: String): String =
        withContext(Dispatchers.IO) {
            val multipart = MultipartBody.Builder()
                .setType(MultipartBody.FORM)
                .addFormDataPart("file", fileName, body)
                .build()
            val builder = Request.Builder().url(urlString).post(multipart)
            grpc.validAccessToken()?.takeIf { it.isNotBlank() }?.let { builder.header("x-auth-token", it) }
            http.newCall(builder.build()).execute().use { resp ->
                if (!resp.isSuccessful) throw IOException("Upload failed: HTTP ${resp.code}")
                val text = resp.body?.string().orEmpty()
                val fileId = runCatching { JSONObject(text).optString("fileId") }.getOrNull()
                if (fileId.isNullOrEmpty()) throw IOException("Upload: no fileId in response")
                fileId
            }
        }

    /** Скачать оригинал во временный файл (для предпросмотра / шеринга). */
    suspend fun download(urlString: String, suggestedName: String): File =
        withContext(Dispatchers.IO) {
            http.newCall(Request.Builder().url(urlString).get().build()).execute().use { resp ->
                if (!resp.isSuccessful) throw IOException("Download failed: HTTP ${resp.code}")
                val name = suggestedName.ifEmpty { UUID.randomUUID().toString() }
                val dest = File(appContext.cacheDir, name)
                if (dest.exists()) dest.delete()
                val source = resp.body ?: throw IOException("Download: empty body")
                source.byteStream().use { input -> dest.outputStream().use { input.copyTo(it) } }
                dest
            }
        }

    private fun uriRequestBody(uri: Uri): RequestBody = object : RequestBody() {
        override fun contentType() = OCTET_STREAM
        override fun contentLength(): Long = querySize(uri)
        override fun writeTo(sink: BufferedSink) {
            appContext.contentResolver.openInputStream(uri)?.use { input ->
                input.copyTo(sink.outputStream())
            } ?: throw IOException("Cannot open $uri")
        }
    }

    private fun querySize(uri: Uri): Long = runCatching {
        appContext.contentResolver.query(uri, arrayOf(OpenableColumns.SIZE), null, null, null)?.use { c ->
            val idx = c.getColumnIndex(OpenableColumns.SIZE)
            if (c.moveToFirst() && idx >= 0 && !c.isNull(idx)) c.getLong(idx) else -1L
        } ?: -1L
    }.getOrDefault(-1L)

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

    data class UploadTarget(val url: String, val fileId: String)
    data class StorageInfo(val used: Long, val limit: Long)

    private companion object {
        val OCTET_STREAM = "application/octet-stream".toMediaTypeOrNull()
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

/** Ошибка PUT части: HTTP-код + тело ответа сервера. */
class UploadPartHttpException(val code: Int, val body: String) : IOException("HTTP $code: $body") {
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

    override fun contentType() = OCTET_STREAM
    override fun contentLength(): Long = length

    override fun writeTo(sink: BufferedSink) {
        RandomAccessFile(file, "r").use { raf ->
            raf.seek(offset)
            val buffer = ByteArray(64 * 1024)
            var sent = 0L
            while (sent < length) {
                val toRead = minOf(buffer.size.toLong(), length - sent).toInt()
                val read = raf.read(buffer, 0, toRead)
                if (read < 0) throw IOException("Unexpected EOF in ${file.path} at ${offset + sent}")
                sink.write(buffer, 0, read)
                sent += read
                onProgress(sent)
            }
        }
    }

    private companion object {
        val OCTET_STREAM = "application/octet-stream".toMediaTypeOrNull()
    }
}
