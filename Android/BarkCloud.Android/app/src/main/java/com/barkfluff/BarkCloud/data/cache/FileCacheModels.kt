package com.barkfluff.BarkCloud.data.cache

import java.io.IOException
import java.security.MessageDigest

data class CacheScope(
    val serverKey: String,
    val accountId: String,
) {
    companion object {
        const val UNKNOWN_ACCOUNT = "unknown-account"
    }
}

enum class CacheCategory(val directoryName: String) {
    PREVIEW("previews"),
    ORIGINAL("originals"),
    JPEG_VIEW("jpeg-views"),
}

data class FileCacheKey(
    val scope: CacheScope,
    val fileId: String,
    val variant: String,
    val category: CacheCategory,
) {
    val storageKey: String by lazy(LazyThreadSafetyMode.NONE) {
        sha256(
            listOf(scope.serverKey, scope.accountId, fileId, variant, category.name)
                .joinToString("") { "${it.length}:$it" },
        )
    }

    companion object {
        fun original(scope: CacheScope, fileId: String) =
            FileCacheKey(scope, fileId, ORIGINAL_VARIANT, CacheCategory.ORIGINAL)

        fun sharedOriginal(scope: CacheScope, fileId: String) =
            FileCacheKey(scope, fileId, SHARED_ORIGINAL_VARIANT, CacheCategory.ORIGINAL)

        fun preview(
            scope: CacheScope,
            fileId: String,
            previewFileId: String?,
            fullUrl: String?,
        ): FileCacheKey {
            val variant = previewFileId?.takeIf { it.isNotBlank() }?.let { "preview:$it" }
                ?: "preview-url:${sha256(requireNotNull(fullUrl) { "A preview URL is required when PreviewFileId is missing" })}"
            return FileCacheKey(scope, fileId, variant, CacheCategory.PREVIEW)
        }

        fun jpegView(scope: CacheScope, fileId: String) =
            jpegView(scope, fileId, null)

        fun jpegView(scope: CacheScope, fileId: String, jpegViewFileId: String?) =
            FileCacheKey(
                scope,
                fileId,
                jpegViewFileId?.takeIf { it.isNotBlank() }?.let { "$JPEG_VIEW_VARIANT:$it" } ?: JPEG_VIEW_VARIANT,
                CacheCategory.JPEG_VIEW,
            )

        private const val ORIGINAL_VARIANT = "original"
        private const val SHARED_ORIGINAL_VARIANT = "shared-original"
        private const val JPEG_VIEW_VARIANT = "jpeg-view"
    }
}

data class FileCacheEntry(
    val key: FileCacheKey,
    val sizeBytes: Long,
    val lastUsedAt: Long,
    val fileName: String,
)

data class FileCacheLoadRequest(
    val key: FileCacheKey,
    val fileName: String,
    val sourceUrl: String? = null,
    val sourceFile: java.io.File? = null,
)

fun interface FileCacheLoader {
    suspend fun load(request: FileCacheLoadRequest, destination: java.io.File)
}

interface FileCacheIndex {
    suspend fun get(storageKey: String): FileCacheEntry?
    suspend fun all(): List<FileCacheEntry>
    suspend fun upsert(entry: FileCacheEntry)
    suspend fun delete(storageKey: String)
    suspend fun clear()

    suspend fun totalSizeBytes(): Long = all().sumOf { it.sizeBytes }

    suspend fun evictionCandidates(): List<FileCacheEntry> = all()
        .sortedWith(compareBy<FileCacheEntry> { it.lastUsedAt }.thenBy { it.key.storageKey })

    suspend fun entriesInScope(scope: CacheScope): List<FileCacheEntry> =
        all().filter { it.key.scope == scope }

    suspend fun rebindScope(oldScope: CacheScope, newScope: CacheScope) {
        entriesInScope(oldScope).forEach { entry ->
            val rebound = entry.copy(key = entry.key.copy(scope = newScope))
            if (get(rebound.key.storageKey) == null) upsert(rebound)
            delete(entry.key.storageKey)
        }
    }
}

class CacheInvalidatedException(message: String) : IOException(message)

class FileCachePin internal constructor(internal val token: Long)

class PinnedFile internal constructor(val file: java.io.File, internal val pin: FileCachePin)

interface FileCachePolicy {
    val maxCacheBytes: Long
    val staleMaxAgeMillis: Long
    var lastSweepAtMillis: Long
}

data class CacheCategoryStats(val sizeBytes: Long, val entryCount: Int)

data class FileCacheStats(
    val categories: Map<CacheCategory, CacheCategoryStats>,
    val totalSizeBytes: Long,
    val entryCount: Int,
    val isOverLimit: Boolean,
    val legacyPreviewSizeBytes: Long,
    val legacyPreviewEntryCount: Int,
)

private fun sha256(value: String): String = MessageDigest.getInstance("SHA-256")
    .digest(value.toByteArray(Charsets.UTF_8))
    .joinToString("") { (it.toInt() and 0xff).toString(16).padStart(2, '0') }
