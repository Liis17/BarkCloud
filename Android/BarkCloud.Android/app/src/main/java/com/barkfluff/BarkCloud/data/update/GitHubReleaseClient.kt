package com.barkfluff.BarkCloud.data.update

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.time.ZonedDateTime
import java.time.format.DateTimeFormatter
import java.util.concurrent.TimeUnit

fun interface AndroidReleaseFetcher {
    suspend fun fetchLatest(): AndroidReleaseMetadata
}

class GitHubReleaseClient(
    private val client: OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(20, TimeUnit.SECONDS)
        .callTimeout(30, TimeUnit.SECONDS)
        .build(),
    private val nowMillis: () -> Long = System::currentTimeMillis,
    private val apiUrl: String = LATEST_RELEASE_URL,
) : AndroidReleaseFetcher {
    override suspend fun fetchLatest(): AndroidReleaseMetadata = withContext(Dispatchers.IO) {
        val apiBytes = getBytes(apiUrl, AndroidReleaseParser.MAX_JSON_BYTES)
        val release = AndroidReleaseParser.parseLatest(apiBytes.toString(Charsets.UTF_8))
        if (release.assets.none { it.name == AndroidReleaseParser.MANIFEST_NAME }) {
            throw MissingUpdaterMetadataException(release.tag)
        }
        val manifestUrl = "https://github.com/Liis17/BarkCloud/releases/download/" +
            "${release.tag}/${AndroidReleaseParser.MANIFEST_NAME}"
        val manifestBytes = getBytes(manifestUrl, AndroidReleaseParser.MAX_JSON_BYTES, release.tag)
        AndroidReleaseParser.parseManifest(manifestBytes, release)
    }

    private fun getBytes(url: String, maximumBytes: Int, missingManifestTag: String? = null): ByteArray {
        val request = Request.Builder()
            .url(url)
            .header("Accept", "application/vnd.github+json")
            .header("User-Agent", "BarkCloud-Android-Updater")
            .get()
            .build()
        client.newCall(request).execute().use { response ->
            if (response.code == 403 || response.code == 429) {
                throw GitHubRateLimitException(
                    retryAtMillis = retryAtMillis(response.header("Retry-After"), response.header("X-RateLimit-Reset")),
                    statusCode = response.code,
                )
            }
            if (!response.isSuccessful) {
                if (response.code == 404 && missingManifestTag != null) {
                    throw MissingUpdaterMetadataException(missingManifestTag)
                }
                throw IOException("GitHub Releases вернул HTTP ${response.code}.")
            }
            val body = response.body ?: throw IOException("GitHub Releases вернул пустой ответ.")
            if (body.contentLength() > maximumBytes) throw IOException("Ответ GitHub Releases слишком большой.")
            val output = ByteArrayOutputStream(minOf(maximumBytes, 16 * 1024))
            body.byteStream().use { input ->
                val buffer = ByteArray(8192)
                while (true) {
                    val count = input.read(buffer)
                    if (count < 0) break
                    if (output.size() + count > maximumBytes) throw IOException("Ответ GitHub Releases слишком большой.")
                    output.write(buffer, 0, count)
                }
            }
            return output.toByteArray()
        }
    }

    private fun retryAtMillis(retryAfter: String?, rateLimitReset: String?): Long {
        retryAfter?.toLongOrNull()?.let { seconds ->
            if (seconds >= 0) return nowMillis() + seconds * 1000
        }
        retryAfter?.let { value ->
            runCatching { ZonedDateTime.parse(value, DateTimeFormatter.RFC_1123_DATE_TIME).toInstant().toEpochMilli() }
                .getOrNull()?.let { return it }
        }
        rateLimitReset?.toLongOrNull()?.let { seconds -> return seconds * 1000 }
        return nowMillis() + FALLBACK_RATE_LIMIT_MILLIS
    }

    companion object {
        const val LATEST_RELEASE_URL = "https://api.github.com/repos/Liis17/BarkCloud/releases/latest"
        private const val FALLBACK_RATE_LIMIT_MILLIS = 60L * 60 * 1000
    }
}

class GitHubRateLimitException(val retryAtMillis: Long, statusCode: Int) :
    IOException("GitHub ограничил запросы (HTTP $statusCode). Повторите проверку после указанного времени.")
