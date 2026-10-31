package com.barkfluff.BarkCloud.data.update

import java.io.File
import java.security.MessageDigest
import java.time.LocalDate

object AndroidUpdatePolicy {
    const val STARTUP_INTERVAL_MILLIS = 24L * 60 * 60 * 1000
    const val PAGE_CACHE_MILLIS = 15L * 60 * 1000
    const val DOWNLOAD_TTL_MILLIS = 7L * 24 * 60 * 60 * 1000

    fun isDue(nowMillis: Long, lastAttemptMillis: Long, intervalMillis: Long): Boolean =
        lastAttemptMillis <= 0 || (nowMillis >= lastAttemptMillis && nowMillis - lastAttemptMillis >= intervalMillis)

    fun isRateLimitActive(nowMillis: Long, retryAtMillis: Long): Boolean =
        retryAtMillis > 0 && nowMillis < retryAtMillis

    fun shouldPrompt(
        release: AndroidReleaseMetadata,
        installedVersionCode: Long,
        nowDay: LocalDate,
        promptedDay: LocalDate?,
    ): Boolean {
        if (release.versionCode <= installedVersionCode) return false
        if (promptedDay == null) return true
        return nowDay.isAfter(promptedDay)
    }

    fun shouldInvalidateReadyFile(ready: AndroidReleaseMetadata, latest: AndroidReleaseMetadata): Boolean =
        ready.tag != latest.tag || ready.versionCode != latest.versionCode ||
            ready.apkFileName != latest.apkFileName || ready.apkSize != latest.apkSize ||
            !ready.sha256.equals(latest.sha256, ignoreCase = true)

    fun shouldDeleteForTtl(nowMillis: Long, createdAtMillis: Long): Boolean =
        createdAtMillis > 0 && nowMillis >= createdAtMillis && nowMillis - createdAtMillis >= DOWNLOAD_TTL_MILLIS

    fun isOwnedUpdatePath(file: File, updatesDirectory: File): Boolean {
        val root = runCatching { updatesDirectory.canonicalFile.toPath() }.getOrNull() ?: return false
        val candidate = runCatching { file.canonicalFile.toPath() }.getOrNull() ?: return false
        return candidate.startsWith(root) && candidate != root
    }
}

data class ApkSignerSet(
    val currentCertificates: Set<String>,
    val certificateHistory: Set<String>,
    val hasMultipleSigners: Boolean,
)

object ApkIntegrity {
    fun verifyFile(file: File, expectedSize: Long, expectedSha256: String) {
        if (!file.isFile || file.length() != expectedSize) {
            throw IllegalArgumentException("Размер загруженного APK не совпадает с манифестом.")
        }
        if (!sha256(file).equals(expectedSha256, ignoreCase = true)) {
            throw IllegalArgumentException("SHA-256 загруженного APK не совпадает с манифестом.")
        }
    }

    fun sha256(file: File): String {
        val digest = MessageDigest.getInstance("SHA-256")
        file.inputStream().buffered().use { input ->
            val buffer = ByteArray(DEFAULT_BUFFER_SIZE)
            while (true) {
                val read = input.read(buffer)
                if (read < 0) break
                digest.update(buffer, 0, read)
            }
        }
        return digest.digest().joinToString("") { "%02x".format(it) }
    }

    fun signingCompatible(candidate: ApkSignerSet, installed: ApkSignerSet): Boolean {
        if (candidate.currentCertificates.isEmpty() || installed.currentCertificates.isEmpty()) return false
        if (candidate.hasMultipleSigners || installed.hasMultipleSigners) {
            return candidate.hasMultipleSigners && installed.hasMultipleSigners &&
                candidate.currentCertificates == installed.currentCertificates
        }
        if (candidate.currentCertificates.size != 1 || installed.currentCertificates.size != 1) return false
        val installedCurrent = installed.currentCertificates.single()
        val candidateCurrent = candidate.currentCertificates.single()
        return candidateCurrent == installedCurrent || installedCurrent in candidate.certificateHistory
    }
}
