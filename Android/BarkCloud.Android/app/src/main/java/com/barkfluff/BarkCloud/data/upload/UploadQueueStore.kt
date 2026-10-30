package com.barkfluff.BarkCloud.data.upload

import android.content.Context
import android.net.Uri
import com.barkfluff.BarkCloud.data.gallery.BackupUploadQueue
import com.barkfluff.BarkCloud.data.gallery.DeviceMedia
import com.barkfluff.BarkCloud.data.gallery.DeviceMediaStore
import com.barkfluff.BarkCloud.data.gallery.MediaCloudState
import com.barkfluff.BarkCloud.data.gallery.MediaCloudStatus
import com.barkfluff.BarkCloud.data.persistence.BarkCloudDatabase
import com.barkfluff.BarkCloud.data.persistence.MediaCloudStateDao
import com.barkfluff.BarkCloud.data.persistence.UploadDao
import com.barkfluff.BarkCloud.net.queryFileName
import androidx.room.withTransaction
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.withContext
import org.json.JSONArray
import java.io.File
import java.io.FileNotFoundException
import java.io.InputStream
import java.security.MessageDigest
import java.util.UUID

internal data class PreparedBackup(val job: UploadJob, val staleSessionId: String?)
private data class StagedBackupBytes(val file: File, val hash: Pair<String, Long>, val mediaAfter: DeviceMedia)

internal interface BackupMediaAccess {
    suspend fun findByKey(mediaKey: String): DeviceMedia?
    fun open(media: DeviceMedia): InputStream?
}

private class MediaStoreBackupMediaAccess(private val context: Context) : BackupMediaAccess {
    override suspend fun findByKey(mediaKey: String): DeviceMedia? = DeviceMediaStore.findByKey(context, mediaKey)
    override fun open(media: DeviceMedia): InputStream? = context.contentResolver.openInputStream(media.uri)
}

/** Persistent source of truth for every outgoing upload. */
class UploadQueueStore(
    private val context: Context,
    private val dao: UploadDao = BarkCloudDatabase.get(context).uploadDao(),
    private val database: BarkCloudDatabase = BarkCloudDatabase.get(context),
) : BackupUploadQueue, ActiveUploadPageSource {
    private val appContext = context.applicationContext
    private var backupMediaAccess: BackupMediaAccess = MediaStoreBackupMediaAccess(appContext)
    private var queueDir = File(appContext.filesDir, "upload_queue")
    private var afterBackupStagePublished: suspend (File) -> Unit = {}
    private var afterBackupStageSaved: suspend (File) -> Unit = {}
    private val mediaDao get() = database.mediaCloudStateDao()

    internal constructor(
        context: Context,
        dao: UploadDao,
        database: BarkCloudDatabase,
        backupMediaAccess: BackupMediaAccess,
        queueDirectory: File,
        afterBackupStagePublished: suspend (File) -> Unit = {},
        afterBackupStageSaved: suspend (File) -> Unit = {},
    ) : this(context, dao, database) {
        this.backupMediaAccess = backupMediaAccess
        queueDir = queueDirectory
        this.afterBackupStagePublished = afterBackupStagePublished
        this.afterBackupStageSaved = afterBackupStageSaved
    }

    val visibleJobs: Flow<List<UploadJob>> = dao.observeVisible()
    val recentJobs: Flow<List<UploadJob>> = dao.observeRecent()

    suspend fun initialize() {
        migrateLegacyQueue()
        migrateV1Jobs()
        dao.deleteCompletedBefore(System.currentTimeMillis() - COMPLETED_RETENTION_MILLIS)
        reconcileBackupStageFiles()
    }

    /**
     * Manual, gallery, album, and share jobs keep their eager app-private staging path.
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

    override suspend fun enqueueBackupIfAbsent(media: DeviceMedia): Boolean = withContext(Dispatchers.IO) {
        val now = System.currentTimeMillis()
        var oldStage: String? = null
        var oldStageId: String? = null
        var inserted = false
        database.withTransaction {
            val state = mediaDao.byKey(media.mediaKey)
            val id = backupJobId(media.mediaKey)
            val existing = dao.byId(id) ?: dao.backupByMediaKey(media.mediaKey)
            val sameState = state != null && state.dateModifiedSeconds == media.dateModifiedSeconds && state.sizeBytes == media.sizeBytes
            val sameJob = existing?.let {
                it.bytesTotal == media.sizeBytes && it.fileName == media.name && it.mimeType == media.mimeType
            } == true
            val existingActive = existing != null && (existing.phase in ACTIVE_PHASES || existing.phase == UploadPhase.PAUSED ||
                existing.phase == UploadPhase.UPLOADED_NOT_ATTACHED)

            if (existingActive) {
                if (state == null) mediaDao.upsert(mediaState(media, null, MediaCloudStatus.QUEUED))
                return@withTransaction
            }
            if (existing != null && sameJob && (sameState || state == null) &&
                existing.phase in setOf(UploadPhase.FAILED, UploadPhase.CANCELLED, UploadPhase.COMPLETED)
            ) return@withTransaction
            if (existing == null && sameState && state?.status == MediaCloudStatus.IN_CLOUD) {
                return@withTransaction
            }
            if (existing == null && sameState && state?.status in setOf(MediaCloudStatus.QUEUED, MediaCloudStatus.UPLOADING)) {
                // Repair a state row whose old upload job was removed or lost.
            }

            oldStage = existing?.stagedFilePath
            oldStageId = existing?.id
            val job = (existing ?: UploadJob(
                id = id,
                source = UploadSource.BACKUP,
                stagedFilePath = null,
                mediaKey = media.mediaKey,
                mediaHash = null,
                fileName = media.name,
                mimeType = media.mimeType,
                destination = UploadDestination.SYSTEM_BY_MEDIA_KIND,
                directoryId = null,
                albumId = null,
                phase = UploadPhase.QUEUED,
                idempotencyKey = existing?.idempotencyKey,
                sessionId = existing?.sessionId,
                partSize = existing?.partSize ?: 0L,
                preparedFileId = existing?.preparedFileId,
                bytesTotal = media.sizeBytes,
                bytesSent = 0L,
                errorMessage = null,
                createdAtMillis = now,
                completedAtMillis = null,
            )).copy(
                source = UploadSource.BACKUP,
                stagedFilePath = null,
                mediaKey = media.mediaKey,
                mediaHash = null,
                fileName = media.name,
                mimeType = media.mimeType,
                destination = UploadDestination.SYSTEM_BY_MEDIA_KIND,
                directoryId = null,
                albumId = null,
                phase = UploadPhase.QUEUED,
                idempotencyKey = existing?.idempotencyKey,
                sessionId = existing?.sessionId,
                partSize = existing?.partSize ?: 0L,
                preparedFileId = existing?.preparedFileId,
                bytesTotal = media.sizeBytes,
                bytesSent = 0L,
                errorMessage = null,
                completedAtMillis = null,
                createdAtMillis = existing?.createdAtMillis ?: now,
            )
            if (existing == null) {
                inserted = dao.insertBackupIfAbsent(job) != -1L
            } else {
                dao.update(job)
                inserted = true
            }
            if (inserted) mediaDao.upsert(mediaState(media, null, MediaCloudStatus.QUEUED))
        }
        if (inserted) oldStage?.let { deleteBackupFile(it, oldStageId) }
        inserted
    }

    internal suspend fun prepareBackup(
        id: String,
        mediaDao: MediaCloudStateDao,
        ensureActive: suspend (DeviceMedia) -> Unit,
    ): PreparedBackup? = withContext(Dispatchers.IO) {
        withBackupJobLock(id) { prepareBackupLocked(id, mediaDao, ensureActive) }
    }

    private suspend fun prepareBackupLocked(
        id: String,
        mediaDao: MediaCloudStateDao,
        ensureActive: suspend (DeviceMedia) -> Unit,
    ): PreparedBackup? {
        var staleSessionId: String? = null
        repeat(MAX_SOURCE_SNAPSHOT_ATTEMPTS) { attempt ->
            val job = dao.byId(id) ?: return null
            if (job.source != UploadSource.BACKUP || job.phase in setOf(UploadPhase.CANCELLED, UploadPhase.COMPLETED, UploadPhase.PAUSED)) {
                return null
            }
            val mediaKey = job.mediaKey ?: throw FileNotFoundException("Backup media key is missing")
            val media = backupMediaAccess.findByKey(mediaKey) ?: throw FileNotFoundException(mediaKey)
            ensureActive(media)

            val state = mediaDao.byKey(mediaKey)
            val sourceChanged = state?.dateModifiedSeconds != media.dateModifiedSeconds ||
                state.sizeBytes != media.sizeBytes || job.bytesTotal != media.sizeBytes ||
                job.fileName != media.name || job.mimeType != media.mimeType
            if (sourceChanged || (job.stagedFilePath != null && !isValidBackupStage(job))) {
                staleSessionId = resetForCurrentMedia(job, media, mediaDao) ?: staleSessionId
            } else if (isValidBackupStage(job)) {
                return PreparedBackup(job, staleSessionId)
            }

            val current = dao.byId(id) ?: return null
            if (current.phase in setOf(UploadPhase.CANCELLED, UploadPhase.COMPLETED, UploadPhase.PAUSED)) return null
            dao.setPhase(id, UploadPhase.HASHING)
            val stagedBytes = try {
                copyAndHashBackup(current, media, ensureActive)
            } catch (changed: BackupMediaChangedException) {
                val latest = changed.currentMedia ?: throw changed
                staleSessionId = resetForCurrentMedia(current, latest, mediaDao) ?: staleSessionId
                if (attempt + 1 == MAX_SOURCE_SNAPSHOT_ATTEMPTS) throw changed
                return@repeat
            }
            if (!sameSource(media, stagedBytes.mediaAfter)) {
                deleteBackupOwnedFiles(id, null)
                staleSessionId = resetForCurrentMedia(requireNotNull(dao.byId(id)), stagedBytes.mediaAfter, mediaDao) ?: staleSessionId
                if (attempt + 1 == MAX_SOURCE_SNAPSHOT_ATTEMPTS) throw BackupMediaChangedException()
                return@repeat
            }
            val staged = stagedBytes.file
            try {
                afterBackupStagePublished(staged)
                val hash = stagedBytes.hash
                if (hash.second != media.sizeBytes) {
                    throw FileNotFoundException("MediaStore size does not match source bytes")
                }
                val currentBeforeSave = requireNotNull(dao.byId(id))
                val resetSession = currentBeforeSave.mediaHash != hash.first &&
                    (currentBeforeSave.sessionId != null || currentBeforeSave.idempotencyKey != null || currentBeforeSave.preparedFileId != null)
                if (resetSession) staleSessionId = currentBeforeSave.sessionId
                val saved = database.withTransaction {
                    val updated = dao.setBackupPrepared(
                        id = id,
                        path = staged.absolutePath,
                        hash = hash.first,
                        bytesTotal = hash.second,
                        mimeType = media.mimeType,
                        resetSession = resetSession,
                    )
                    if (updated > 0) mediaDao.upsert(mediaState(media, hash.first, MediaCloudStatus.QUEUED))
                    updated > 0
                }
                if (!saved) {
                    deleteBackupStageIfUntracked(id, staged)
                    return null
                }
                afterBackupStageSaved(staged)
                return PreparedBackup(requireNotNull(dao.byId(id)), staleSessionId)
            } catch (error: Throwable) {
                deleteBackupStageIfUntracked(id, staged)
                throw error
            }
        }
        return null
    }

    override suspend fun activePage(afterId: String?, limit: Int): List<UploadJob> = dao.nextActivePage(afterId, limit)
    suspend fun activeCount(): Int = dao.activeCount()
    suspend fun stagedBackupCount(): Int = dao.stagedBackupCount()
    suspend fun byId(id: String): UploadJob? = dao.byId(id)

    /** Release inactive copies first, then trim old queued stages without touching live transfers. */
    internal suspend fun releaseExcessBackupStages(maximum: Int) = withContext(Dispatchers.IO) {
        while (true) {
            val inactive = dao.inactiveStagedBackupPage(STAGE_RELEASE_BATCH_SIZE)
            if (inactive.isEmpty()) break
            if (releaseBackupStages(inactive) == 0) break
        }
        var stagedCount = dao.stagedBackupCount()
        while (stagedCount > maximum.coerceAtLeast(0)) {
            val excess = dao.releasableBackupStagePage(STAGE_RELEASE_BATCH_SIZE)
            if (excess.isEmpty()) break
            var released = 0
            for (job in excess) {
                if (stagedCount <= maximum.coerceAtLeast(0)) break
                if (releaseBackupStage(job)) {
                    stagedCount--
                    released++
                }
            }
            if (released == 0) break
        }
    }

    private suspend fun releaseBackupStages(jobs: List<UploadJob>): Int {
        var released = 0
        for (job in jobs) if (releaseBackupStage(job)) released++
        return released
    }

    private suspend fun releaseBackupStage(job: UploadJob): Boolean = withBackupJobLock(job.id) {
        val path = job.stagedFilePath ?: return@withBackupJobLock false
        if (dao.releaseBackupStage(job.id, path, job.phase) == 0) return@withBackupJobLock false
        deleteBackupOwnedFiles(job.id, path)
        true
    }

    suspend fun setProgress(id: String, sent: Long, total: Long, phase: UploadPhase) =
        dao.setProgress(id, sent.coerceAtLeast(0L), total.coerceAtLeast(0L), phase)

    suspend fun setPhase(id: String, phase: UploadPhase, error: String? = null) = dao.setPhase(id, phase, error)
    suspend fun setHash(id: String, hash: String, bytesTotal: Long) = dao.setHash(id, hash, bytesTotal)
    suspend fun prepareUploadSession(id: String, idempotencyKey: String, replaceExisting: Boolean) =
        dao.prepareUploadSession(id, idempotencyKey, replaceExisting)
    suspend fun bindSession(id: String, sessionId: String, idempotencyKey: String, fileId: String, partSize: Long): Int =
        dao.bindSession(id, sessionId, idempotencyKey, fileId, partSize)
    suspend fun failAfterQuota(id: String, error: String?) = dao.failAfterQuota(id, error)

    suspend fun complete(job: UploadJob) {
        val current = dao.byId(job.id) ?: return
        if (dao.markCompleted(job.id, System.currentTimeMillis()) > 0) {
            if (current.source == UploadSource.BACKUP) deleteBackupOwnedFiles(current.id, current.stagedFilePath)
            else current.stagedFilePath?.let(::deleteQueueFile)
        }
    }

    /** Bytes are remote; release only the app-private staged copy. */
    suspend fun dropStaging(id: String) {
        val current = dao.byId(id) ?: return
        if (dao.clearStagedPath(id) > 0) {
            if (current.source == UploadSource.BACKUP) deleteBackupOwnedFiles(current.id, current.stagedFilePath)
            else current.stagedFilePath?.let(::deleteQueueFile)
        }
    }

    suspend fun failBackupAndDropStage(id: String, error: String?) {
        val current = dao.byId(id) ?: return
        if (dao.failBackupAndDropStage(id, error) > 0) deleteBackupOwnedFiles(id, current.stagedFilePath)
    }

    /**
     * Retry keeps a good staged backup and server session. Terminal backup cleanup
     * clears both, so the explicit retry starts from the current MediaStore URI.
     */
    suspend fun retry(id: String) {
        val job = dao.byId(id) ?: return
        if (job.phase !in setOf(UploadPhase.FAILED, UploadPhase.UPLOADED_NOT_ATTACHED, UploadPhase.PAUSED)) return
        val phase = job.retryPhase()
        database.withTransaction {
            val retried = dao.retry(id, job.phase, phase) > 0
            if (retried && job.source == UploadSource.BACKUP) {
                mediaDao.byKey(requireNotNull(job.mediaKey))?.let { mediaDao.upsert(it.copy(status = MediaCloudStatus.QUEUED)) }
            }
        }
    }

    /** Cancellation is terminal; conditional queue writes cannot restore the row afterward. */
    suspend fun cancel(id: String) {
        val job = dao.byId(id) ?: return
        if (dao.cancel(id) > 0) {
            if (job.source == UploadSource.BACKUP) deleteBackupOwnedFiles(job.id, job.stagedFilePath)
            else job.stagedFilePath?.let(::deleteQueueFile)
        }
    }

    suspend fun cancelledWithSessions(): List<UploadJob> = dao.cancelledWithSession()
    suspend fun clearSession(id: String) = dao.clearSession(id)

    suspend fun pauseBackup() = dao.pauseBackup()
    suspend fun pauseBackupJob(id: String) = dao.pauseBackupJob(id)
    suspend fun resumeBackup() = dao.resumeBackup()

    suspend fun clear() = withContext(Dispatchers.IO) {
        dao.deleteAll()
        queueDir.deleteRecursively()
        queueDir.mkdirs()
    }

    private suspend fun resetForCurrentMedia(job: UploadJob, media: DeviceMedia, states: MediaCloudStateDao): String? {
        var reset = false
        val oldSession = job.sessionId
        database.withTransaction {
            val latest = dao.byId(job.id) ?: return@withTransaction
            if (latest.phase in setOf(UploadPhase.CANCELLED, UploadPhase.COMPLETED, UploadPhase.PAUSED)) return@withTransaction
            reset = dao.resetBackupForChangedMedia(job.id, media.name, media.mimeType, media.sizeBytes) > 0
            if (reset) states.upsert(mediaState(media, null, MediaCloudStatus.QUEUED))
        }
        if (reset) deleteBackupOwnedFiles(job.id, job.stagedFilePath)
        return if (reset) oldSession else null
    }

    private suspend fun copyAndHashBackup(
        job: UploadJob,
        media: DeviceMedia,
        ensureActive: suspend (DeviceMedia) -> Unit,
    ): StagedBackupBytes {
        queueDir.mkdirs()
        val part = backupPartFile(job.id)
        val destination = backupStageFile(job.id)
        var latestMedia: DeviceMedia? = null
        try {
            val hash = BackupStager.stage(
                part = part,
                destination = destination,
                expectedSize = media.sizeBytes,
                openSource = { backupMediaAccess.open(media) },
                verifySource = {
                    val current = backupMediaAccess.findByKey(media.mediaKey)
                        ?: throw FileNotFoundException(media.mediaKey)
                    latestMedia = current
                    ensureActive(current)
                    if (!sameSource(media, current)) throw BackupMediaChangedException(current)
                },
                ensureActive = { ensureActive(media) },
            )
            return StagedBackupBytes(destination, hash, latestMedia ?: media)
        } catch (error: Throwable) {
            if (error !is CancellationException) {
                dao.setPhase(job.id, UploadPhase.QUEUED, error.message)
            }
            throw error
        }
    }

    private fun isValidBackupStage(job: UploadJob): Boolean {
        val path = job.stagedFilePath ?: return false
        val file = File(path)
        return file.isFile && isInsideQueue(file) && file.length() == job.bytesTotal && job.mediaHash != null
    }

    private fun sameSource(first: DeviceMedia, second: DeviceMedia): Boolean =
        first.mediaKey == second.mediaKey && first.name == second.name &&
            first.dateModifiedSeconds == second.dateModifiedSeconds && first.sizeBytes == second.sizeBytes &&
            first.mimeType == second.mimeType && first.folderKey == second.folderKey

    private fun mediaState(media: DeviceMedia, hash: String?, status: MediaCloudStatus) = MediaCloudState(
        mediaKey = media.mediaKey,
        mediaId = media.id,
        isVideo = media.isVideo,
        dateModifiedSeconds = media.dateModifiedSeconds,
        sizeBytes = media.sizeBytes,
        hash = hash,
        status = status,
        cloudFileId = null,
    )

    private fun backupJobId(mediaKey: String): String {
        val digest = MessageDigest.getInstance("SHA-256").digest(mediaKey.toByteArray(Charsets.UTF_8))
        return "backup-" + digest.joinToString("") { "%02x".format(it) }
    }

    private fun backupPartFile(id: String) = File(queueDir, "backup-$id.part")
    private fun backupStageFile(id: String) = File(queueDir, "backup-$id.stage")

    private suspend fun reconcileBackupStageFiles() = withContext(Dispatchers.IO) {
        queueDir.listFiles()?.forEach { file ->
            val id = backupJobIdForOwnedFile(file) ?: return@forEach
            withBackupJobLock(id) {
                val job = dao.byId(id)
                val tracked = file.name.endsWith(".stage") && job?.source == UploadSource.BACKUP &&
                    job.phase !in setOf(UploadPhase.COMPLETED, UploadPhase.CANCELLED) &&
                    job.stagedFilePath == file.absolutePath
                if (!tracked) file.delete()
            }
        }
    }

    private fun backupJobIdForOwnedFile(file: File): String? {
        if (!isInsideQueue(file)) return null
        val extension = when {
            file.name.endsWith(".part") -> ".part"
            file.name.endsWith(".stage") -> ".stage"
            else -> return null
        }
        val id = file.name.removePrefix("backup-").removeSuffix(extension)
        return id.takeIf { BACKUP_JOB_ID.matches(it) }
    }

    private suspend fun deleteBackupStageIfUntracked(id: String, file: File) = withContext(NonCancellable) {
        val stagedFilePath = try {
            dao.byId(id)?.stagedFilePath
        } catch (_: Exception) {
            return@withContext
        }
        if (stagedFilePath != file.absolutePath) deleteBackupFile(file.absolutePath, id)
    }

    private suspend fun <T> withBackupJobLock(id: String, action: suspend () -> T): T {
        val lock = BACKUP_JOB_LOCKS[(id.hashCode() and Int.MAX_VALUE) % BACKUP_JOB_LOCKS.size]
        lock.lock()
        try {
            return action()
        } finally {
            lock.unlock()
        }
    }

    private fun deleteBackupOwnedFiles(id: String, oldPath: String?) {
        backupPartFile(id).delete()
        backupStageFile(id).delete()
        oldPath?.let { deleteBackupFile(it, id) }
    }

    private fun deleteBackupFile(path: String?, id: String?) {
        if (path == null) return
        if (id == null) return
        val file = File(path)
        if (!isInsideQueue(file)) return
        if (file.name.startsWith("backup-$id.") || file.name.startsWith("$id-")) {
            file.delete()
        }
    }

    private fun isInsideQueue(file: File): Boolean = runCatching {
        file.canonicalFile.parentFile == queueDir.canonicalFile
    }.getOrDefault(false)

    private fun deleteQueueFile(path: String) {
        val file = File(path)
        if (isInsideQueue(file)) file.delete()
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

    /** One-time V1→V2 migration cancels multipart jobs while retaining terminal history. */
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
        const val MAX_SOURCE_SNAPSHOT_ATTEMPTS = 2
        const val STAGE_RELEASE_BATCH_SIZE = 32
        val BACKUP_JOB_ID = Regex("backup-[0-9a-f]{64}")
        val BACKUP_JOB_LOCKS = Array(32) { Mutex() }
        val ACTIVE_PHASES = setOf(
            UploadPhase.QUEUED,
            UploadPhase.HASHING,
            UploadPhase.CREATING_SESSION,
            UploadPhase.UPLOADING,
            UploadPhase.COMPLETING,
            UploadPhase.PROCESSING,
            UploadPhase.ATTACHING,
        )
    }
}
