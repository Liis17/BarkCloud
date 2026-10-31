package com.barkfluff.BarkCloud.data.update

import android.app.DownloadManager
import android.content.Context
import android.net.Uri

enum class UpdateDownloadStatus { PENDING, RUNNING, PAUSED, SUCCESSFUL, FAILED }

data class UpdateDownloadRecord(
    val id: Long,
    val title: String,
    val status: UpdateDownloadStatus,
    val reason: Int,
    val localUri: String?,
    val downloadedBytes: Long,
    val totalBytes: Long,
)

interface UpdateDownloadGateway {
    fun enqueue(attemptId: String, metadata: AndroidReleaseMetadata, fileName: String): Long
    fun get(id: Long): UpdateDownloadRecord?
    fun rowsForAttempt(attemptId: String): List<UpdateDownloadRecord>
    fun managedRows(): List<UpdateDownloadRecord>
    fun remove(id: Long)
}

class AndroidDownloadManagerGateway(private val context: Context) : UpdateDownloadGateway {
    private val manager = context.getSystemService(DownloadManager::class.java)

    override fun enqueue(attemptId: String, metadata: AndroidReleaseMetadata, fileName: String): Long {
        val request = DownloadManager.Request(Uri.parse(metadata.downloadUrl))
            .setTitle(title(attemptId))
            .setDescription("BarkCloud ${metadata.versionName}")
            .setMimeType(APK_MIME_TYPE)
            .setAllowedOverMetered(true)
            .setAllowedOverRoaming(false)
            .setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE)
            .setDestinationInExternalFilesDir(context, null, "updates/$fileName")
        return manager.enqueue(request)
    }

    override fun get(id: Long): UpdateDownloadRecord? = query(DownloadManager.Query().setFilterById(id)).firstOrNull()

    override fun rowsForAttempt(attemptId: String): List<UpdateDownloadRecord> =
        managedRows().filter { it.title == title(attemptId) }

    override fun managedRows(): List<UpdateDownloadRecord> = query(DownloadManager.Query())

    override fun remove(id: Long) {
        manager.remove(id)
    }

    private fun query(query: DownloadManager.Query): List<UpdateDownloadRecord> {
        val cursor = manager.query(query) ?: return emptyList()
        cursor.use {
            val idColumn = it.getColumnIndexOrThrow(DownloadManager.COLUMN_ID)
            val titleColumn = it.getColumnIndexOrThrow(DownloadManager.COLUMN_TITLE)
            val statusColumn = it.getColumnIndexOrThrow(DownloadManager.COLUMN_STATUS)
            val reasonColumn = it.getColumnIndexOrThrow(DownloadManager.COLUMN_REASON)
            val uriColumn = it.getColumnIndexOrThrow(DownloadManager.COLUMN_LOCAL_URI)
            val downloadedColumn = it.getColumnIndexOrThrow(DownloadManager.COLUMN_BYTES_DOWNLOADED_SO_FAR)
            val totalColumn = it.getColumnIndexOrThrow(DownloadManager.COLUMN_TOTAL_SIZE_BYTES)
            val records = ArrayList<UpdateDownloadRecord>(it.count)
            while (it.moveToNext()) {
                val status = when (it.getInt(statusColumn)) {
                    DownloadManager.STATUS_PENDING -> UpdateDownloadStatus.PENDING
                    DownloadManager.STATUS_RUNNING -> UpdateDownloadStatus.RUNNING
                    DownloadManager.STATUS_PAUSED -> UpdateDownloadStatus.PAUSED
                    DownloadManager.STATUS_SUCCESSFUL -> UpdateDownloadStatus.SUCCESSFUL
                    else -> UpdateDownloadStatus.FAILED
                }
                records += UpdateDownloadRecord(
                    id = it.getLong(idColumn),
                    title = it.getString(titleColumn).orEmpty(),
                    status = status,
                    reason = it.getInt(reasonColumn),
                    localUri = if (it.isNull(uriColumn)) null else it.getString(uriColumn),
                    downloadedBytes = it.getLong(downloadedColumn).coerceAtLeast(0),
                    totalBytes = it.getLong(totalColumn).coerceAtLeast(0),
                )
            }
            return records
        }
    }

    companion object {
        const val TITLE_PREFIX = "BarkCloud update "
        const val APK_MIME_TYPE = "application/vnd.android.package-archive"

        fun title(attemptId: String): String = "$TITLE_PREFIX$attemptId"
    }
}

internal data class UpdateDownloadAttempt(
    val attemptId: String,
    val metadata: AndroidReleaseMetadata,
    val createdAtMillis: Long,
    val fileName: String,
    val downloadId: Long? = null,
    val ready: Boolean = false,
)

internal interface AndroidUpdateStore {
    fun cachedRelease(): AndroidReleaseMetadata?
    fun cacheRelease(metadata: AndroidReleaseMetadata)
    fun markMetadataUnavailable(tag: String?)
    fun metadataUnavailableTag(): String?
    fun lastAttemptMillis(): Long
    fun lastStartupAttemptMillis(): Long
    fun rateLimitUntilMillis(): Long
    fun recordAttempt(nowMillis: Long, startup: Boolean)
    fun recordRateLimit(untilMillis: Long)
    fun promptedDay(tag: String): String?
    fun markPromptShown(tag: String, day: String)
    fun attempt(): UpdateDownloadAttempt?
    fun saveAttempt(attempt: UpdateDownloadAttempt)
    fun clearAttempt()
}

internal class AndroidUpdatePreferences(context: Context) : AndroidUpdateStore {
    private val prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    override fun cachedRelease(): AndroidReleaseMetadata? = prefs.getString(KEY_CACHED_RELEASE, null)
        ?.let { runCatching { AndroidReleaseMetadata.fromJson(it) }.getOrNull() }

    override fun cacheRelease(metadata: AndroidReleaseMetadata) {
        check(prefs.edit()
            .putString(KEY_CACHED_RELEASE, metadata.toJson())
            .putLong(KEY_RATE_LIMIT_UNTIL, 0)
            .remove(KEY_METADATA_UNAVAILABLE_TAG)
            .commit()) { "Не удалось сохранить метаданные обновления." }
    }

    override fun markMetadataUnavailable(tag: String?) {
        check(prefs.edit().putString(KEY_METADATA_UNAVAILABLE_TAG, tag ?: "unknown").commit()) {
            "Не удалось сохранить состояние метаданных обновления."
        }
    }

    override fun metadataUnavailableTag(): String? = prefs.getString(KEY_METADATA_UNAVAILABLE_TAG, null)

    override fun lastAttemptMillis(): Long = prefs.getLong(KEY_LAST_ATTEMPT, 0)
    override fun lastStartupAttemptMillis(): Long = prefs.getLong(KEY_LAST_STARTUP_ATTEMPT, 0)
    override fun rateLimitUntilMillis(): Long = prefs.getLong(KEY_RATE_LIMIT_UNTIL, 0)

    override fun recordAttempt(nowMillis: Long, startup: Boolean) {
        val edit = prefs.edit().putLong(KEY_LAST_ATTEMPT, nowMillis)
        if (startup) edit.putLong(KEY_LAST_STARTUP_ATTEMPT, nowMillis)
        check(edit.commit()) { "Не удалось сохранить время проверки обновлений." }
    }

    override fun recordRateLimit(untilMillis: Long) {
        check(prefs.edit().putLong(KEY_RATE_LIMIT_UNTIL, untilMillis).commit()) {
            "Не удалось сохранить ограничение запросов GitHub."
        }
    }

    override fun promptedDay(tag: String): String? = prefs.getString(promptedDayKey(tag), null)

    override fun markPromptShown(tag: String, day: String) {
        check(prefs.edit().putString(promptedDayKey(tag), day).commit()) {
            "Не удалось сохранить состояние уведомления об обновлении."
        }
    }

    override fun attempt(): UpdateDownloadAttempt? = prefs.getString(KEY_ATTEMPT, null)?.let { encoded ->
        runCatching {
            val json = JSONObjectCompat(encoded)
            UpdateDownloadAttempt(
                attemptId = json.string("attemptId"),
                metadata = AndroidReleaseMetadata.fromJson(json.string("metadata")),
                createdAtMillis = json.long("createdAtMillis"),
                fileName = json.string("fileName"),
                downloadId = json.nullableLong("downloadId"),
                ready = json.boolean("ready"),
            )
        }.getOrNull()
    }

    override fun saveAttempt(attempt: UpdateDownloadAttempt) {
        val json = org.json.JSONObject()
            .put("attemptId", attempt.attemptId)
            .put("metadata", attempt.metadata.toJson())
            .put("createdAtMillis", attempt.createdAtMillis)
            .put("fileName", attempt.fileName)
            .put("downloadId", attempt.downloadId ?: org.json.JSONObject.NULL)
            .put("ready", attempt.ready)
        check(prefs.edit().putString(KEY_ATTEMPT, json.toString()).commit()) {
            "Не удалось сохранить состояние загрузки обновления."
        }
    }

    override fun clearAttempt() {
        check(prefs.edit().remove(KEY_ATTEMPT).commit()) {
            "Не удалось очистить состояние загрузки обновления."
        }
    }

    private class JSONObjectCompat(value: String) {
        private val json = org.json.JSONObject(value)
        fun string(key: String): String = json.getString(key)
        fun long(key: String): Long = json.getLong(key)
        fun boolean(key: String): Boolean = json.getBoolean(key)
        fun nullableLong(key: String): Long? = if (json.isNull(key)) null else json.getLong(key)
    }

    private fun promptedDayKey(tag: String): String = "prompted_day_${tag.replace('.', '_')}"

    companion object {
        const val PREFS_NAME = "barkcloud_android_updates"
        private const val KEY_CACHED_RELEASE = "cached_release"
        private const val KEY_LAST_ATTEMPT = "last_attempt"
        private const val KEY_LAST_STARTUP_ATTEMPT = "last_startup_attempt"
        private const val KEY_RATE_LIMIT_UNTIL = "rate_limit_until"
        private const val KEY_METADATA_UNAVAILABLE_TAG = "metadata_unavailable_tag"
        private const val KEY_ATTEMPT = "attempt"
    }
}
