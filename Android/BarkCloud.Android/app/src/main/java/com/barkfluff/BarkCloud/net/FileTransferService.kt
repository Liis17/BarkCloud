package com.barkfluff.BarkCloud.net

import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
import barkcloud.files.FilesApiOuterClass.GetTempDownloadUrlRequest
import barkcloud.files.FilesApiOuterClass.GetUploadUrlRequest
import barkcloud.files.FilesApiOuterClass.GetUserStorageInfoRequest
import barkcloud.files.FilesApiOuterClass.UploadFileType
import com.barkfluff.BarkCloud.data.GlobalParam
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
import okio.source
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.util.UUID

/**
 * Передача байтов файлов: gRPC `FilesApi` (ссылки/квота) + обычный HTTP upload/download
 * на готовые URL, которые возвращает сервер. Загрузка/скачивание идут НЕ через gRPC, а
 * POST/GET на `:7025/web/{upload|download}/{id}` через [InsecureHttp] (self-signed TLS).
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

    // MARK: HTTP

    /** Залить содержимое [uri] (стримингом). Возвращает file_id ИЗ ОТВЕТА (учёт дедупликации). */
    suspend fun upload(uri: Uri, fileName: String, urlString: String): String =
        upload(uriRequestBody(uri), fileName, urlString)

    /** Залить локальный staged-файл из app storage. */
    suspend fun upload(file: File, fileName: String, urlString: String): String =
        upload(fileRequestBody(file), fileName, urlString)

    /** Залить готовые байты (например, аватар). */
    suspend fun upload(bytes: ByteArray, fileName: String, urlString: String): String =
        upload(bytes.toRequestBody(OCTET_STREAM), fileName, urlString)

    private suspend fun upload(body: RequestBody, fileName: String, urlString: String): String =
        withContext(Dispatchers.IO) {
            val multipart = MultipartBody.Builder()
                .setType(MultipartBody.FORM)
                .addFormDataPart("file", fileName, body)
                .build()
            val builder = Request.Builder().url(urlString).post(multipart)
            globalParam.accessToken?.takeIf { it.isNotBlank() }?.let { builder.header("x-auth-token", it) }
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
                sink.writeAll(input.source())
            } ?: throw IOException("Cannot open $uri")
        }
    }

    private fun fileRequestBody(file: File): RequestBody = object : RequestBody() {
        override fun contentType() = OCTET_STREAM
        override fun contentLength(): Long = file.length()
        override fun writeTo(sink: BufferedSink) {
            file.inputStream().use { input -> sink.writeAll(input.source()) }
        }
    }

    private fun querySize(uri: Uri): Long = runCatching {
        appContext.contentResolver.query(uri, arrayOf(OpenableColumns.SIZE), null, null, null)?.use { c ->
            val idx = c.getColumnIndex(OpenableColumns.SIZE)
            if (c.moveToFirst() && idx >= 0 && !c.isNull(idx)) c.getLong(idx) else -1L
        } ?: -1L
    }.getOrDefault(-1L)

    data class UploadTarget(val url: String, val fileId: String)
    data class StorageInfo(val used: Long, val limit: Long)

    private companion object {
        val OCTET_STREAM = "application/octet-stream".toMediaTypeOrNull()
    }
}
