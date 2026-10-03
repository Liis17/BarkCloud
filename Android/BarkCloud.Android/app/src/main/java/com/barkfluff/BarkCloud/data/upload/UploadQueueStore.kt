package com.barkfluff.BarkCloud.data.upload

import android.content.Context
import android.net.Uri
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
        migrateLegacyQueue()
        migrateV1Jobs()
        dao.deleteCompletedBefore(System.currentTimeMillis() - COMPLETED_RETENTION_MILLIS)
    }

    /**
     * Всегда копирует источник в app-private staging: Upload 2.0 требует повторного
     * чтения регионов файла (SHA-256 до create, окна частей после) — стримить из
     * content:// с skip по смещению ненадёжно.
     */
    suspend fun enqueue(
        uri: Uri,
        fileName: String = queryFileName(appContext, uri),
        source: UploadSource = UploadSource.MANUAL,
        destination: UploadDestination = UploadDestination.SYSTEM_BY_MEDIA_KIND,
        directoryId: String? = null,
        albumId: String? = null,
        mediaKey: String? = null,
        mediaHash: String? = null,
    ): UploadJob = withContext(Dispatchers.IO) {
        val id = UUID.randomUUID().toString()
        val staged = stage(uri, id, fileName)
        val job = UploadJob(
            id = id,
            source = source,
            stagedFilePath = staged.absolutePath,
            mediaKey = mediaKey,
            mediaHash = mediaHash,
            fileName = fileName.ifBlank { "file" },
            mimeType = appContext.contentResolver.getType(uri),
            destination = destination,
            directoryId = directoryId,
            albumId = albumId,
            phase = UploadPhase.QUEUED,
            idempotencyKey = UUID.randomUUID().toString(),
            sessionId = null,
            partSize = 0L,
            preparedFileId = null,
            bytesTotal = staged.length(),
            bytesSent = 0L,
            errorMessage = null,
            createdAtMillis = System.currentTimeMillis(),
            completedAtMillis = null,
        )
        dao.insert(job)
        job
    }

    suspend fun activeJobs(): List<UploadJob> = dao.activeJobs()
    suspend fun byId(id: String): UploadJob? = dao.byId(id)

    suspend fun setProgress(id: String, sent: Long, total: Long, phase: UploadPhase) =
        dao.setProgress(id, sent.coerceAtLeast(0L), total.coerceAtLeast(0L), phase)

    suspend fun setPhase(id: String, phase: UploadPhase, error: String? = null) = dao.setPhase(id, phase, error)
    suspend fun setHash(id: String, hash: String, bytesTotal: Long) = dao.setHash(id, hash, bytesTotal)
    suspend fun bindSession(id: String, sessionId: String, idempotencyKey: String, fileId: String, partSize: Long) =
        dao.bindSession(id, sessionId, idempotencyKey, fileId, partSize)

    suspend fun complete(job: UploadJob) {
        dao.markCompleted(job.id, System.currentTimeMillis())
        job.stagedFilePath?.let { File(it).delete() }
    }

    /** Байты уже на сервере — локальная копия больше не нужна, задача ждёт только привязки. */
    suspend fun dropStaging(id: String) {
        dao.byId(id)?.stagedFilePath?.let { File(it).delete() }
    }

    /**
     * Retry: с живой сессией — снова в UPLOADING (worker сделает resume и сам разберёт
     * серверное состояние: UPLOADING/PROCESSING/READY); без сессии — с начала.
     * UPLOADED_NOT_ATTACHED повторяет только привязку.
     */
    suspend fun retry(id: String) {
        val job = dao.byId(id) ?: return
        val phase = when {
            job.phase == UploadPhase.UPLOADED_NOT_ATTACHED -> UploadPhase.ATTACHING
            job.sessionId != null -> UploadPhase.UPLOADING
            else -> UploadPhase.QUEUED
        }
        dao.update(job.copy(phase = phase, errorMessage = null))
    }

    /**
     * Отмена: помечает CANCELLED и удаляет staging. Серверную сессию отменяет worker,
     * заметив фазу (или при следующем запуске через [cancelledWithSessions]).
     */
    suspend fun cancel(id: String) {
        val job = dao.byId(id) ?: return
        dao.update(job.copy(phase = UploadPhase.CANCELLED))
        job.stagedFilePath?.let { File(it).delete() }
    }

    suspend fun cancelledWithSessions(): List<UploadJob> = dao.cancelledWithSession()
    suspend fun clearSession(id: String) = dao.clearSession(id)

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
                    stagedFilePath = path,
                    mediaKey = null,
                    mediaHash = null,
                    fileName = item.optString("fileName").ifBlank { file.name },
                    mimeType = null,
                    destination = if (directoryId == null) UploadDestination.SYSTEM_BY_MEDIA_KIND else UploadDestination.DIRECTORY,
                    directoryId = directoryId,
                    albumId = item.optString("albumId").ifBlank { null },
                    phase = UploadPhase.QUEUED,
                    idempotencyKey = UUID.randomUUID().toString(),
                    sessionId = null,
                    partSize = 0L,
                    preparedFileId = null,
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

    /**
     * Одноразовая миграция V1→V2 (паритет с iOS): активные legacy-задачи отменяются
     * вместе со staging — multipart-задачи несовместимы с V2-воркером. Терминальные
     * строки остаются как история.
     */
    private suspend fun migrateV1Jobs() = withContext(Dispatchers.IO) {
        val prefs = appContext.getSharedPreferences(V2_MIGRATION_PREFS, Context.MODE_PRIVATE)
        if (prefs.getBoolean(V2_MIGRATION_MARKER, false)) return@withContext
        dao.v1ActiveJobs().forEach { job -> job.stagedFilePath?.let { File(it).delete() } }
        dao.cancelV1Jobs(appContext.getString(com.barkfluff.BarkCloud.R.string.upload_v1_cancelled_message))
        prefs.edit().putBoolean(V2_MIGRATION_MARKER, true).apply()
    }

    private companion object {
        const val LEGACY_PREFS_NAME = "barkcloud_upload_queue"
        const val LEGACY_ITEMS = "items"
        const val V2_MIGRATION_PREFS = "barkcloud_upload2"
        const val V2_MIGRATION_MARKER = "migration.v1"
        const val COMPLETED_RETENTION_MILLIS = 24L * 60L * 60L * 1000L
    }
}
