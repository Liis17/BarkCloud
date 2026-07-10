package com.barkfluff.BarkCloud.data.upload

import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
import com.barkfluff.BarkCloud.data.persistence.BarkCloudDatabase
import com.barkfluff.BarkCloud.data.persistence.UploadDao
import com.barkfluff.BarkCloud.net.queryFileName
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.withContext
import org.json.JSONArray
import java.io.File
import java.util.UUID

/** Persistent source of truth for every outgoing upload. */
class UploadQueueStore(
    private val context: Context,
    private val dao: UploadDao = BarkCloudDatabase.get(context).uploadDao(),
) {
    private val appContext = context.applicationContext
    private val queueDir = File(appContext.filesDir, "upload_queue")

    val visibleJobs: Flow<List<UploadJob>> = dao.observeVisible()
    val recentJobs: Flow<List<UploadJob>> = dao.observeRecent()

    suspend fun initialize() {
        dao.recoverInterruptedUploads()
        dao.recoverInterruptedAttachments()
        migrateLegacyQueue()
        dao.deleteCompletedBefore(System.currentTimeMillis() - COMPLETED_RETENTION_MILLIS)
    }

    suspend fun enqueue(
        uri: Uri,
        fileName: String = queryFileName(appContext, uri),
        source: UploadSource = UploadSource.MANUAL,
        destination: UploadDestination = UploadDestination.SYSTEM_BY_MEDIA_KIND,
        directoryId: String? = null,
        albumId: String? = null,
        mediaKey: String? = null,
        mediaHash: String? = null,
        stageSource: Boolean = true,
    ): UploadJob = withContext(Dispatchers.IO) {
        val id = UUID.randomUUID().toString()
        val staged = if (stageSource) stage(uri, id, fileName) else null
        val bytes = staged?.length() ?: querySize(uri)
        val job = UploadJob(
            id = id,
            source = source,
            sourceUri = if (stageSource) null else uri.toString(),
            stagedFilePath = staged?.absolutePath,
            mediaKey = mediaKey,
            mediaHash = mediaHash,
            fileName = fileName.ifBlank { "file" },
            mimeType = appContext.contentResolver.getType(uri),
            destination = destination,
            directoryId = directoryId,
            albumId = albumId,
            phase = UploadPhase.QUEUED,
            preparedFileId = null,
            uploadUrl = null,
            bytesTotal = bytes.coerceAtLeast(0L),
            bytesSent = 0L,
            errorMessage = null,
            createdAtMillis = System.currentTimeMillis(),
            completedAtMillis = null,
        )
        dao.insert(job)
        job
    }

    suspend fun nextActive(): UploadJob? = dao.nextActive()
    suspend fun activeJobs(): List<UploadJob> = dao.activeJobs()
    suspend fun byId(id: String): UploadJob? = dao.byId(id)
    suspend fun setProgress(id: String, sent: Long, total: Long, phase: UploadPhase) =
        dao.setProgress(id, sent.coerceAtLeast(0L), total.coerceAtLeast(0L), phase)

    suspend fun markUploaded(id: String, fileId: String) = dao.markUploaded(id, fileId)
    suspend fun prepareUpload(id: String, fileId: String, uploadUrl: String) = dao.prepareUpload(id, fileId, uploadUrl)
    suspend fun setPhase(id: String, phase: UploadPhase, error: String? = null) = dao.setPhase(id, phase, error)

    suspend fun complete(job: UploadJob) {
        dao.markCompleted(job.id, System.currentTimeMillis())
        job.stagedFilePath?.let { File(it).delete() }
    }

    suspend fun retry(id: String) {
        val job = dao.byId(id) ?: return
        dao.update(job.copy(phase = if (job.preparedFileId == null) UploadPhase.QUEUED else UploadPhase.UPLOADED, errorMessage = null))
    }

    suspend fun cancel(id: String) {
        val job = dao.byId(id) ?: return
        dao.update(job.copy(phase = UploadPhase.CANCELLED))
        job.stagedFilePath?.let { File(it).delete() }
    }

    suspend fun pauseBackup() = dao.pauseBackup()
    suspend fun resumeBackup() = dao.resumeBackup()
    suspend fun activeBackup(): List<UploadJob> = dao.activeBackup()

    suspend fun clear() = withContext(Dispatchers.IO) {
        dao.deleteAll()
        queueDir.deleteRecursively()
        queueDir.mkdirs()
    }

    private fun stage(uri: Uri, id: String, name: String): File {
        queueDir.mkdirs()
        val safeName = name.ifBlank { "file" }.replace(Regex("[\\\\/:*?\"<>|]"), "_")
        val destination = File(queueDir, "$id-$safeName")
        appContext.contentResolver.openInputStream(uri)?.use { input ->
            destination.outputStream().use { output -> input.copyTo(output) }
        } ?: error("Cannot open $uri")
        return destination
    }

    private fun querySize(uri: Uri): Long = runCatching {
        appContext.contentResolver.query(uri, arrayOf(OpenableColumns.SIZE), null, null, null)?.use { cursor ->
            val column = cursor.getColumnIndex(OpenableColumns.SIZE)
            if (cursor.moveToFirst() && column >= 0 && !cursor.isNull(column)) cursor.getLong(column) else 0L
        } ?: 0L
    }.getOrDefault(0L)

    private suspend fun migrateLegacyQueue() = withContext(Dispatchers.IO) {
        val prefs = appContext.getSharedPreferences(LEGACY_PREFS_NAME, Context.MODE_PRIVATE)
        val raw = prefs.getString(LEGACY_ITEMS, null) ?: return@withContext
        val items = runCatching { JSONArray(raw) }.getOrNull() ?: return@withContext
        for (index in 0 until items.length()) {
            val item = items.optJSONObject(index) ?: continue
            val path = item.optString("filePath")
            val file = File(path)
            if (!file.exists()) continue
            val directoryId = item.optString("directoryId").ifBlank { null }
            dao.insert(
                UploadJob(
                    id = item.optString("id").ifBlank { UUID.randomUUID().toString() },
                    source = UploadSource.MANUAL,
                    sourceUri = null,
                    stagedFilePath = path,
                    mediaKey = null,
                    mediaHash = null,
                    fileName = item.optString("fileName").ifBlank { file.name },
                    mimeType = null,
                    destination = if (directoryId == null) UploadDestination.SYSTEM_BY_MEDIA_KIND else UploadDestination.DIRECTORY,
                    directoryId = directoryId,
                    albumId = item.optString("albumId").ifBlank { null },
                    phase = UploadPhase.QUEUED,
                    preparedFileId = null,
                    uploadUrl = null,
                    bytesTotal = file.length(),
                    bytesSent = 0L,
                    errorMessage = null,
                    createdAtMillis = System.currentTimeMillis(),
                    completedAtMillis = null,
                ),
            )
        }
        prefs.edit().clear().apply()
    }

    private companion object {
        const val LEGACY_PREFS_NAME = "barkcloud_upload_queue"
        const val LEGACY_ITEMS = "items"
        const val COMPLETED_RETENTION_MILLIS = 24L * 60L * 60L * 1000L
    }
}
