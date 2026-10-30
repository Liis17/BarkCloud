package com.barkfluff.BarkCloud.data.upload

import android.system.ErrnoException
import android.system.OsConstants
import com.barkfluff.BarkCloud.grpc.errorCode
import com.barkfluff.BarkCloud.grpc.grpcFailureOrNull
import com.barkfluff.BarkCloud.net.UploadPartHttpException
import io.grpc.Status
import java.io.FileNotFoundException
import java.io.IOException

internal enum class UploadFailureDisposition {
    ITEM_TERMINAL,
    RETRY_AND_STOP_CLAIMING,
    QUOTA_STOP_CLAIMING,
    STORAGE_STOP_CLAIMING,
    PERMISSION_STOP_CLAIMING,
}

/** Shared failure classification keeps a bad source local while protecting the queue tail. */
internal object UploadFailurePolicy {
    fun classify(
        error: Throwable,
        source: UploadSource,
        explicitTerminal: Boolean = false,
        permissionUnavailable: Boolean = false,
    ): UploadFailureDisposition {
        val causes = generateSequence(error) { it.cause }.toList()
        val grpcErrors = causes.mapNotNull { it.grpcFailureOrNull() }
        if (grpcErrors.any { it.errorCode() == UploadErrorCodes.QUOTA_EXCEEDED }) {
            return UploadFailureDisposition.QUOTA_STOP_CLAIMING
        }
        if (causes.any(::isStorageExhausted)) return UploadFailureDisposition.STORAGE_STOP_CLAIMING
        if (permissionUnavailable || source == UploadSource.BACKUP && causes.any { it is SecurityException }) {
            return UploadFailureDisposition.PERMISSION_STOP_CLAIMING
        }
        if (explicitTerminal || causes.any { it is FileNotFoundException || it is BackupMediaChangedException }) {
            return UploadFailureDisposition.ITEM_TERMINAL
        }

        val partError = causes.filterIsInstance<UploadPartHttpException>().firstOrNull()
        if (partError != null) {
            return if (partError.retryable) UploadFailureDisposition.RETRY_AND_STOP_CLAIMING
            else UploadFailureDisposition.ITEM_TERMINAL
        }
        val grpcError = grpcErrors.firstOrNull()
        if (grpcError != null) {
            return if (grpcError.status.code in setOf(Status.Code.UNAVAILABLE, Status.Code.DEADLINE_EXCEEDED, Status.Code.RESOURCE_EXHAUSTED)) {
                UploadFailureDisposition.RETRY_AND_STOP_CLAIMING
            } else {
                UploadFailureDisposition.ITEM_TERMINAL
            }
        }
        return UploadFailureDisposition.RETRY_AND_STOP_CLAIMING
    }

    private fun isStorageExhausted(error: Throwable): Boolean {
        if (error is ErrnoException && error.errno == OsConstants.ENOSPC) return true
        return error is IOException && listOf("ENOSPC", "no space left", "disk quota exceeded").any { marker ->
            error.message?.contains(marker, ignoreCase = true) == true
        }
    }
}
