package com.barkfluff.BarkCloud.data.upload

import com.barkfluff.BarkCloud.net.UploadSessionPart
import kotlin.math.ceil

/** Геометрия частей Upload 2.0: partSize назначает сервер, клиент обязан использовать его. */
object UploadPartMath {

    fun partCount(totalSize: Long, partSize: Long): Int {
        require(totalSize > 0 && partSize > 0) { "totalSize=$totalSize partSize=$partSize" }
        return ceil(totalSize.toDouble() / partSize).toInt()
    }

    fun partOffset(partNumber: Int, partSize: Long): Long = (partNumber - 1L) * partSize

    fun partLength(partNumber: Int, totalSize: Long, partSize: Long): Long {
        val offset = partOffset(partNumber, partSize)
        return minOf(partSize, totalSize - offset)
    }

    /** Часть подтверждена сервером: с ETag и ровно ожидаемого размера. */
    fun isConfirmed(part: UploadSessionPart, expectedSize: Long): Boolean =
        part.hasEtag && part.size == expectedSize

    /** Суммарные байты подтверждённых частей (стартовое значение прогресса после resume). */
    fun confirmedBytes(uploaded: List<UploadSessionPart>, totalSize: Long, partSize: Long): Long =
        uploaded.filter { isConfirmed(it, partLength(it.partNumber, totalSize, partSize)) }.sumOf { it.size }

    /** Номера частей, которые нужно отправить/повторить. */
    fun missingPartNumbers(uploaded: List<UploadSessionPart>, totalSize: Long, partSize: Long): List<Int> {
        val confirmed = uploaded
            .filter { isConfirmed(it, partLength(it.partNumber, totalSize, partSize)) }
            .map { it.partNumber }
            .toSet()
        return (1..partCount(totalSize, partSize)).filter { it !in confirmed }
    }
}
