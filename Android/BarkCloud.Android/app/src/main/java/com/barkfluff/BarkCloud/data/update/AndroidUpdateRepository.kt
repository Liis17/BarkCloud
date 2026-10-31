package com.barkfluff.BarkCloud.data.update

import android.content.Context
import android.os.Build
import androidx.work.ExistingWorkPolicy
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.workDataOf
import com.barkfluff.BarkCloud.BuildConfig
import java.io.File
import java.net.URI
import java.time.LocalDate
import java.time.ZoneId
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

enum class AndroidUpdatePhase {
    IDLE,
    CHECKING,
    AVAILABLE,
    UP_TO_DATE,
    UNAVAILABLE_METADATA,
    DOWNLOADING,
    WAITING,
    VERIFYING,
    READY,
    FAILED,
}

data class AndroidUpdateUiState(
    val currentVersionName: String = BuildConfig.VERSION_NAME,
    val currentVersionCode: Long = BuildConfig.VERSION_CODE.toLong(),
    val release: AndroidReleaseMetadata? = null,
    val latestUnavailableTag: String? = null,
    val phase: AndroidUpdatePhase = AndroidUpdatePhase.IDLE,
    val downloadedBytes: Long = 0,
    val totalBytes: Long = 0,
    val message: String? = null,
    val retryAtMillis: Long? = null,
)

enum class UpdateVerificationResult { VERIFIED, NOT_READY, OBSOLETE }

fun interface VerificationWorkScheduler {
    fun schedule(attemptId: String)
}

private class WorkManagerVerificationScheduler(private val context: Context) : VerificationWorkScheduler {
    override fun schedule(attemptId: String) {
        val request = OneTimeWorkRequestBuilder<AndroidUpdateVerificationWorker>()
            .setInputData(workDataOf(AndroidUpdateVerificationWorker.KEY_ATTEMPT_ID to attemptId))
            .build()
        WorkManager.getInstance(context).enqueueUniqueWork(
            "android-update-verify-$attemptId",
            ExistingWorkPolicy.KEEP,
            request,
        )
    }
}

class AndroidUpdateRepository internal constructor(
    context: Context,
    private val preferences: AndroidUpdateStore = AndroidUpdatePreferences(context.applicationContext),
    private val releaseClient: AndroidReleaseFetcher = GitHubReleaseClient(),
    private val downloads: UpdateDownloadGateway = AndroidDownloadManagerGateway(context.applicationContext),
    private val apkVerifier: UpdateApkVerifier = AndroidApkVerifier(context.applicationContext),
    private val clock: () -> Long = System::currentTimeMillis,
    private val verificationScheduler: VerificationWorkScheduler = WorkManagerVerificationScheduler(context.applicationContext),
) {
    private val appContext = context.applicationContext
    private val mutex = Mutex()
    private val installedVersionCode: Long by lazy { installedPackageVersionCode() }
    private val _state = MutableStateFlow(initialState())
    val state: StateFlow<AndroidUpdateUiState> = _state.asStateFlow()

    fun cachedRelease(): AndroidReleaseMetadata? = preferences.cachedRelease()

    fun markStartupPromptShown(release: AndroidReleaseMetadata) {
        preferences.markPromptShown(release.tag, currentDay().toString())
    }

    fun canRequestInstallPackages(): Boolean = Build.VERSION.SDK_INT < Build.VERSION_CODES.O ||
        appContext.packageManager.canRequestPackageInstalls()

    suspend fun checkForPage(force: Boolean) = mutex.withLock {
        reconcileDownloadLocked()
        val now = clock()
        if (AndroidUpdatePolicy.isRateLimitActive(now, preferences.rateLimitUntilMillis())) {
            showCheckProblem(RATE_LIMIT_MESSAGE, preferences.rateLimitUntilMillis())
            return@withLock
        }
        if (!force && !AndroidUpdatePolicy.isDue(
                now,
                preferences.lastAttemptMillis(),
                AndroidUpdatePolicy.PAGE_CACHE_MILLIS,
            )
        ) return@withLock
        preferences.recordAttempt(now, startup = false)
        fetchLatestLocked()
    }

    suspend fun checkForStartupPrompt(): AndroidReleaseMetadata? = mutex.withLock {
        reconcileDownloadLocked()
        val day = currentDay()
        val cachedCandidate = promptCandidate(day)
        val now = clock()
        if (!AndroidUpdatePolicy.isDue(
                now,
                preferences.lastStartupAttemptMillis(),
                AndroidUpdatePolicy.STARTUP_INTERVAL_MILLIS,
            )
        ) return@withLock cachedCandidate
        preferences.recordAttempt(now, startup = true)
        if (AndroidUpdatePolicy.isRateLimitActive(now, preferences.rateLimitUntilMillis())) {
            showCheckProblem(RATE_LIMIT_MESSAGE, preferences.rateLimitUntilMillis())
            return@withLock cachedCandidate
        }
        val latest = fetchLatestLocked()
        if (latest != null) return@withLock promptCandidate(day)
        if (preferences.metadataUnavailableTag() != null) return@withLock null
        cachedCandidate
    }

    suspend fun reconcile() = mutex.withLock {
        reconcileDownloadLocked()
    }

    suspend fun startDownload() = mutex.withLock {
        val release = preferences.cachedRelease()
            ?: throw IllegalStateException("Сначала проверьте наличие обновлений.")
        if (preferences.metadataUnavailableTag() != null) {
            throw IllegalStateException("Метаданные обновления для последнего релиза недоступны.")
        }
        if (release.versionCode <= installedVersionCode) {
            throw IllegalStateException("Для установленной версии обновление не требуется.")
        }
        val existing = preferences.attempt()
        if (existing != null) {
            reconcileDownloadLocked()
            val active = preferences.attempt()
            if (active != null && !active.ready) {
                throw IllegalStateException("Загрузка обновления уже выполняется.")
            }
            if (active != null) discardAttemptLocked(active)
        }
        val updates = updatesDirectory(create = true)
            ?: throw IllegalStateException("Внешнее хранилище недоступно. Подключите его и повторите попытку.")
        val attemptId = UUID.randomUUID().toString()
        val fileName = "${release.tag}-$attemptId.apk"
        val attempt = UpdateDownloadAttempt(attemptId, release, clock(), fileName)
        preferences.saveAttempt(attempt)
        _state.value = stateFor(release, AndroidUpdatePhase.DOWNLOADING)
        try {
            val id = downloads.enqueue(attemptId, release, fileName)
            preferences.saveAttempt(attempt.copy(downloadId = id))
            _state.value = stateFor(release, AndroidUpdatePhase.DOWNLOADING)
        } catch (error: Exception) {
            downloads.rowsForAttempt(attemptId).forEach { downloads.remove(it.id) }
            safeDelete(File(updates, fileName), updates)
            preferences.clearAttempt()
            _state.value = stateFor(release, AndroidUpdatePhase.FAILED, error.message ?: DOWNLOAD_FAILED_MESSAGE)
            throw IllegalStateException(error.message ?: DOWNLOAD_FAILED_MESSAGE, error)
        }
    }

    suspend fun cancelDownload() = mutex.withLock {
        val attempt = preferences.attempt() ?: return@withLock
        removeAttemptRows(attempt)
        updatesDirectory(create = false)?.let { safeDelete(File(it, attempt.fileName), it) }
        preferences.clearAttempt()
        _state.value = stateFor(attempt.metadata, availablePhase(attempt.metadata))
    }

    suspend fun deleteDownloadedUpdate() = mutex.withLock {
        preferences.attempt()?.let { discardAttemptLocked(it) }
        cleanupUpdateFilesLocked(keepFile = null)
        _state.value = stateFor(preferences.cachedRelease(), availablePhase(preferences.cachedRelease()))
    }

    suspend fun prepareInstall(): File = mutex.withLock {
        val attempt = preferences.attempt()
            ?: throw IllegalStateException("Скачанный APK больше не доступен.")
        if (!attempt.ready) throw IllegalStateException("Проверка APK ещё не завершена.")
        if (attempt.metadata.versionCode <= installedVersionCode) {
            discardAttemptLocked(attempt)
            throw IllegalStateException("Установленная версия уже новее этого APK.")
        }
        val updates = updatesDirectory(create = false)
            ?: throw IllegalStateException("Внешнее хранилище недоступно.")
        val file = File(updates, attempt.fileName).canonicalFile
        requireOwnedFile(file, updates, attempt)
        apkVerifier.verify(file, attempt.metadata)
        file
    }

    fun scheduleVerificationForDownload(downloadId: Long) {
        val attempt = preferences.attempt() ?: return
        if (attempt.downloadId == downloadId) verificationScheduler.schedule(attempt.attemptId)
    }

    suspend fun verifyAttempt(attemptId: String): UpdateVerificationResult = mutex.withLock {
        val attempt = preferences.attempt() ?: return@withLock UpdateVerificationResult.OBSOLETE
        if (attempt.attemptId != attemptId || attempt.downloadId == null) {
            return@withLock UpdateVerificationResult.OBSOLETE
        }
        val row = downloads.get(attempt.downloadId)
        if (row == null || row.title != AndroidDownloadManagerGateway.title(attempt.attemptId)) {
            failAttemptLocked(attempt, DOWNLOAD_MISSING_MESSAGE)
            return@withLock UpdateVerificationResult.OBSOLETE
        }
        if (row.status == UpdateDownloadStatus.PENDING || row.status == UpdateDownloadStatus.RUNNING ||
            row.status == UpdateDownloadStatus.PAUSED
        ) return@withLock UpdateVerificationResult.NOT_READY
        if (row.status == UpdateDownloadStatus.FAILED) {
            failAttemptLocked(attempt, downloadFailure(row.reason))
            return@withLock UpdateVerificationResult.OBSOLETE
        }
        try {
            val updates = updatesDirectory(create = false)
                ?: throw IllegalStateException("Внешнее хранилище недоступно.")
            val localUri = row.localUri
                ?: throw IllegalArgumentException("DownloadManager не вернул путь к APK.")
            val file = localFile(localUri)
                ?: throw IllegalArgumentException("DownloadManager не вернул путь к APK.")
            requireOwnedFile(file, updates, attempt)
            apkVerifier.verify(file, attempt.metadata)
            preferences.saveAttempt(attempt.copy(ready = true))
            _state.value = stateFor(attempt.metadata, AndroidUpdatePhase.READY)
            UpdateVerificationResult.VERIFIED
        } catch (error: Exception) {
            if (error is CancellationException) throw error
            failAttemptLocked(attempt, error.message ?: APK_INVALID_MESSAGE)
            UpdateVerificationResult.OBSOLETE
        }
    }

    suspend fun cleanupAfterPackageReplacement() = mutex.withLock {
        cleanupLocked()
    }

    private suspend fun fetchLatestLocked(): AndroidReleaseMetadata? {
        val previous = preferences.cachedRelease()
        _state.value = stateFor(previous, AndroidUpdatePhase.CHECKING)
        return try {
            val latest = releaseClient.fetchLatest()
            preferences.cacheRelease(latest)
            preferences.attempt()?.let { attempt ->
                if (AndroidUpdatePolicy.shouldInvalidateReadyFile(attempt.metadata, latest)) {
                    discardAttemptLocked(attempt)
                }
            }
            reconcileDownloadLocked()
            latest
        } catch (error: Exception) {
            if (error is CancellationException) throw error
            when (error) {
                is GitHubRateLimitException -> preferences.recordRateLimit(error.retryAtMillis)
                is MissingUpdaterMetadataException -> {
                    preferences.markMetadataUnavailable(error.latestTag)
                    preferences.attempt()?.let { discardAttemptLocked(it) }
                }
            }
            showCheckProblem(
                message = error.message ?: "Не удалось проверить обновления.",
                retryAt = (error as? GitHubRateLimitException)?.retryAtMillis,
                unavailable = error is MissingUpdaterMetadataException,
            )
            null
        }
    }

    private suspend fun reconcileDownloadLocked() {
        val attempt = preferences.attempt()
        if (attempt != null && !validAttempt(attempt)) {
            discardAttemptLocked(attempt)
            cleanupUpdateFilesLocked(keepFile = null)
            _state.value = stateFor(preferences.cachedRelease(), AndroidUpdatePhase.FAILED, APK_INVALID_MESSAGE)
            return
        }
        if (attempt != null && installedVersionCode >= attempt.metadata.versionCode) {
            discardAttemptLocked(attempt)
            cleanupUpdateFilesLocked(keepFile = null)
            _state.value = stateFor(preferences.cachedRelease(), availablePhase(preferences.cachedRelease()))
            return
        }
        cleanupExpiredFilesLocked(attempt)
        val current = preferences.attempt()
        if (current == null) {
            cleanupOrphanRowsLocked(null)
            val cached = preferences.cachedRelease()
            val unavailable = preferences.metadataUnavailableTag() != null
            _state.value = stateFor(
                cached,
                if (unavailable) AndroidUpdatePhase.UNAVAILABLE_METADATA else availablePhase(cached),
            )
            return
        }
        var active = current
        if (active.downloadId == null) {
            val rows = downloads.rowsForAttempt(active.attemptId)
            val match = rows.firstOrNull { rowMatchesAttempt(it, active) }
            if (match == null) {
                rows.forEach { downloads.remove(it.id) }
                updatesDirectory(create = false)?.let { safeDelete(File(it, active.fileName), it) }
                preferences.clearAttempt()
                cleanupOrphanRowsLocked(null)
                _state.value = stateFor(active.metadata, AndroidUpdatePhase.FAILED, DOWNLOAD_MISSING_MESSAGE)
                return
            }
            rows.filter { it.id != match.id }.forEach { downloads.remove(it.id) }
            active = active.copy(downloadId = match.id)
            preferences.saveAttempt(active)
        }
        cleanupOrphanRowsLocked(active)
        val row = downloads.get(requireNotNull(active.downloadId))
        if (row == null || row.title != AndroidDownloadManagerGateway.title(active.attemptId)) {
            failAttemptLocked(active, DOWNLOAD_MISSING_MESSAGE)
            return
        }
        when (row.status) {
            UpdateDownloadStatus.PENDING, UpdateDownloadStatus.RUNNING -> {
                _state.value = stateFor(
                    active.metadata,
                    AndroidUpdatePhase.DOWNLOADING,
                    downloadedBytes = row.downloadedBytes,
                    totalBytes = row.totalBytes.takeIf { it > 0 } ?: active.metadata.apkSize,
                )
            }
            UpdateDownloadStatus.PAUSED -> {
                _state.value = stateFor(
                    active.metadata,
                    AndroidUpdatePhase.WAITING,
                    pauseReason(row.reason),
                    downloadedBytes = row.downloadedBytes,
                    totalBytes = row.totalBytes.takeIf { it > 0 } ?: active.metadata.apkSize,
                )
            }
            UpdateDownloadStatus.SUCCESSFUL -> {
                if (active.ready) {
                    val updates = updatesDirectory(create = false)
                    val file = row.localUri?.let(::localFile)
                    if (updates != null && file != null && runCatching { requireOwnedFile(file, updates, active) }.isSuccess && file.isFile) {
                        _state.value = stateFor(active.metadata, AndroidUpdatePhase.READY)
                    } else {
                        failAttemptLocked(active, DOWNLOAD_MISSING_MESSAGE)
                    }
                } else {
                    _state.value = stateFor(active.metadata, AndroidUpdatePhase.VERIFYING)
                    verificationScheduler.schedule(active.attemptId)
                }
            }
            UpdateDownloadStatus.FAILED -> failAttemptLocked(active, downloadFailure(row.reason))
        }
    }

    private suspend fun cleanupLocked() {
        val now = clock()
        val attempt = preferences.attempt()
        if (attempt != null && installedVersionCode >= attempt.metadata.versionCode) {
            discardAttemptLocked(attempt)
            cleanupUpdateFilesLocked(keepFile = null)
        } else if (attempt != null && AndroidUpdatePolicy.shouldDeleteForTtl(now, attempt.createdAtMillis)) {
            discardAttemptLocked(attempt)
        }
        val retained = preferences.attempt()
        cleanupOrphanRowsLocked(retained)
        cleanupUpdateFilesLocked(retained?.fileName)
        reconcileDownloadLocked()
    }

    private suspend fun cleanupExpiredFilesLocked(attempt: UpdateDownloadAttempt?) {
        if (attempt != null && AndroidUpdatePolicy.shouldDeleteForTtl(clock(), attempt.createdAtMillis)) {
            discardAttemptLocked(attempt)
            return
        }
        cleanupUpdateFilesLocked(attempt?.fileName)
    }

    private fun cleanupOrphanRowsLocked(keep: UpdateDownloadAttempt?) {
        val updates = updatesDirectory(create = false)
        val keepId = keep?.downloadId
        val keepTitle = keep?.let { AndroidDownloadManagerGateway.title(it.attemptId) }
        for (row in downloads.managedRows()) {
            if (!row.title.startsWith(AndroidDownloadManagerGateway.TITLE_PREFIX)) continue
            if (row.id == keepId || row.title == keepTitle) continue
            val attemptId = row.title.removePrefix(AndroidDownloadManagerGateway.TITLE_PREFIX)
            if (!UUID_PATTERN.matches(attemptId)) continue
            val file = row.localUri?.let(::localFile)
            if (file == null || (updates != null && AndroidUpdatePolicy.isOwnedUpdatePath(file, updates))) {
                downloads.remove(row.id)
            }
        }
    }

    private suspend fun discardAttemptLocked(attempt: UpdateDownloadAttempt) {
        removeAttemptRows(attempt)
        val updates = updatesDirectory(create = false)
        if (updates != null) safeDelete(File(updates, attempt.fileName), updates)
        preferences.clearAttempt()
    }

    private fun removeAttemptRows(attempt: UpdateDownloadAttempt) {
        val rows = downloads.rowsForAttempt(attempt.attemptId)
        val known = attempt.downloadId?.let(downloads::get)
            ?.takeIf { it.title == AndroidDownloadManagerGateway.title(attempt.attemptId) }
        (rows + listOfNotNull(known)).distinctBy { it.id }.forEach { downloads.remove(it.id) }
    }

    private suspend fun failAttemptLocked(attempt: UpdateDownloadAttempt, message: String) {
        removeAttemptRows(attempt)
        updatesDirectory(create = false)?.let { safeDelete(File(it, attempt.fileName), it) }
        preferences.clearAttempt()
        _state.value = stateFor(attempt.metadata, AndroidUpdatePhase.FAILED, message)
    }

    private fun cleanupUpdateFilesLocked(keepFile: String?) {
        val updates = updatesDirectory(create = false) ?: return
        updates.listFiles()?.forEach { file ->
            if (file.name != keepFile) safeDelete(file, updates)
        }
    }

    private fun updatesDirectory(create: Boolean): File? {
        val external = appContext.getExternalFilesDir(null) ?: return null
        val updates = File(external, "updates")
        if (create && !updates.exists() && !updates.mkdirs()) return null
        if (!updates.exists() && !create) return updates
        return runCatching { updates.canonicalFile }.getOrNull()
    }

    private fun safeDelete(file: File, updates: File) {
        if (AndroidUpdatePolicy.isOwnedUpdatePath(file, updates)) file.delete()
    }

    private fun requireOwnedFile(file: File, updates: File, attempt: UpdateDownloadAttempt) {
        val expected = File(updates, attempt.fileName).canonicalFile
        require(AndroidUpdatePolicy.isOwnedUpdatePath(file, updates) && file.canonicalFile == expected) {
            "DownloadManager вернул APK за пределами папки обновлений."
        }
    }

    private fun rowMatchesAttempt(row: UpdateDownloadRecord, attempt: UpdateDownloadAttempt): Boolean {
        if (row.title != AndroidDownloadManagerGateway.title(attempt.attemptId)) return false
        val uri = row.localUri ?: return true
        val updates = updatesDirectory(create = false) ?: return false
        val file = localFile(uri) ?: return false
        return runCatching { requireOwnedFile(file, updates, attempt) }.isSuccess
    }

    private fun localFile(value: String): File? {
        val uri = runCatching { URI(value) }.getOrNull() ?: return null
        if (uri.scheme != "file") return null
        val path = uri.path ?: return null
        val normalizedPath = if (File.separatorChar == '\\' && Regex("^/[A-Za-z]:/.*").matches(path)) {
            path.drop(1).replace('/', '\\')
        } else {
            path
        }
        return File(normalizedPath)
    }

    private fun validAttempt(attempt: UpdateDownloadAttempt): Boolean = runCatching {
        UUID_PATTERN.matches(attempt.attemptId) &&
            attempt.fileName == "${attempt.metadata.tag}-${attempt.attemptId}.apk" &&
            AndroidReleaseParser.versionCode(attempt.metadata.tag) == attempt.metadata.versionCode
    }.getOrDefault(false)

    private fun promptCandidate(day: LocalDate): AndroidReleaseMetadata? {
        if (preferences.metadataUnavailableTag() != null) return null
        val release = preferences.cachedRelease() ?: return null
        val promptedDay = preferences.promptedDay(release.tag)?.let { runCatching { LocalDate.parse(it) }.getOrNull() }
        return release.takeIf {
            AndroidUpdatePolicy.shouldPrompt(
                release = it,
                installedVersionCode = installedVersionCode,
                nowDay = day,
                promptedDay = promptedDay,
            )
        }
    }

    private fun currentDay(): LocalDate = java.time.Instant.ofEpochMilli(clock()).atZone(ZoneId.systemDefault()).toLocalDate()

    private fun availablePhase(release: AndroidReleaseMetadata?): AndroidUpdatePhase = when {
        preferences.metadataUnavailableTag() != null -> AndroidUpdatePhase.UNAVAILABLE_METADATA
        release == null -> AndroidUpdatePhase.IDLE
        release.versionCode > installedVersionCode -> AndroidUpdatePhase.AVAILABLE
        else -> AndroidUpdatePhase.UP_TO_DATE
    }

    private fun stateFor(
        release: AndroidReleaseMetadata?,
        phase: AndroidUpdatePhase,
        message: String? = null,
        downloadedBytes: Long = 0,
        totalBytes: Long = 0,
    ): AndroidUpdateUiState = AndroidUpdateUiState(
        release = release,
        phase = phase,
        message = message,
        downloadedBytes = downloadedBytes,
        totalBytes = totalBytes,
        latestUnavailableTag = preferences.metadataUnavailableTag(),
        retryAtMillis = preferences.rateLimitUntilMillis().takeIf {
            AndroidUpdatePolicy.isRateLimitActive(clock(), it)
        },
    )

    private fun publishCached(
        message: String? = null,
        retryAt: Long? = null,
        unavailable: Boolean = preferences.metadataUnavailableTag() != null,
    ) {
        val release = preferences.cachedRelease()
        _state.value = stateFor(
            release,
            if (unavailable) AndroidUpdatePhase.UNAVAILABLE_METADATA else availablePhase(release),
            message,
        ).copy(retryAtMillis = retryAt)
    }

    private suspend fun showCheckProblem(message: String, retryAt: Long? = null, unavailable: Boolean = false) {
        if (preferences.attempt() != null && !unavailable) {
            reconcileDownloadLocked()
            _state.value = _state.value.copy(message = message, retryAtMillis = retryAt)
        } else {
            publishCached(message, retryAt, unavailable)
        }
    }

    private fun initialState(): AndroidUpdateUiState {
        val release = preferences.cachedRelease()
        val attempt = preferences.attempt()
        val phase = when {
            preferences.metadataUnavailableTag() != null -> AndroidUpdatePhase.UNAVAILABLE_METADATA
            attempt?.ready == true -> AndroidUpdatePhase.READY
            attempt != null -> AndroidUpdatePhase.IDLE
            else -> availablePhase(release)
        }
        return stateFor(release, phase)
    }

    private fun installedPackageVersionCode(): Long {
        @Suppress("DEPRECATION")
        return appContext.packageManager.getPackageInfo(appContext.packageName, 0).longVersionCode
    }

    private fun pauseReason(reason: Int): String = when (reason) {
        android.app.DownloadManager.PAUSED_WAITING_TO_RETRY -> "Ожидание повторной попытки загрузки."
        android.app.DownloadManager.PAUSED_WAITING_FOR_NETWORK -> "Ожидание подключения к сети."
        android.app.DownloadManager.PAUSED_QUEUED_FOR_WIFI -> "Ожидание Wi-Fi. Для загрузки разрешена мобильная сеть, но не роуминг."
        android.app.DownloadManager.PAUSED_UNKNOWN -> "Загрузка временно приостановлена Android."
        else -> "Загрузка временно приостановлена Android (код $reason)."
    }

    private fun downloadFailure(reason: Int): String =
        "Android DownloadManager остановил загрузку (код $reason). Проверьте сеть и свободное место, затем повторите попытку."

    companion object {
        private val UUID_PATTERN = Regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-4[0-9a-fA-F]{3}-[89aAbB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$")
        const val RATE_LIMIT_MESSAGE = "GitHub временно ограничил запросы. Повторная проверка будет доступна позже."
        const val DOWNLOAD_FAILED_MESSAGE = "Не удалось начать загрузку обновления. Проверьте свободное место и повторите попытку."
        const val DOWNLOAD_MISSING_MESSAGE = "Загрузка больше не найдена. Запустите её ещё раз вручную."
        const val APK_INVALID_MESSAGE = "Не удалось проверить загруженный APK. Удалите его и повторите загрузку."
    }
}
