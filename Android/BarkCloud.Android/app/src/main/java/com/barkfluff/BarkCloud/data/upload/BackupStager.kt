package com.barkfluff.BarkCloud.data.upload

import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import com.barkfluff.BarkCloud.data.gallery.DeviceMedia
import java.io.File
import java.io.FileNotFoundException
import java.io.FileOutputStream
import java.io.InputStream
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.security.MessageDigest

/** Streams one lazy backup source into its private part file, verifies it, then atomically publishes it. */
internal object BackupStager {
    private const val BUFFER_BYTES = 256 * 1024
    private const val PHASE_CHECK_INTERVAL_BYTES = 4L * 1024L * 1024L

    suspend fun stage(
        part: File,
        destination: File,
        expectedSize: Long,
        openSource: () -> InputStream?,
        verifySource: suspend () -> Unit,
        ensureActive: suspend () -> Unit,
    ): Pair<String, Long> {
        part.parentFile?.mkdirs()
        part.delete()
        destination.delete()
        try {
            verifySource()
            val coroutineContext = currentCoroutineContext()
            val input = try {
                openSource() ?: throw FileNotFoundException("MediaStore source cannot be opened")
            } catch (error: FileNotFoundException) {
                throw error
            } catch (error: java.io.IOException) {
                throw BackupSourceReadException(error)
            }
            input.use { source ->
                FileOutputStream(part).use { output ->
                    val buffer = ByteArray(BUFFER_BYTES)
                    var bytesSincePhaseCheck = 0L
                    while (true) {
                        coroutineContext.ensureActive()
                        if (bytesSincePhaseCheck >= PHASE_CHECK_INTERVAL_BYTES) {
                            ensureActive()
                            bytesSincePhaseCheck = 0L
                        }
                        val read = try {
                            source.read(buffer)
                        } catch (error: java.io.IOException) {
                            throw BackupSourceReadException(error)
                        }
                        if (read < 0) break
                        output.write(buffer, 0, read)
                        bytesSincePhaseCheck += read
                    }
                    output.fd.sync()
                }
            }
            ensureActive()
            verifySource()
            val result = hashAndSize(part, ensureActive)
            ensureActive()
            verifySource()
            if (result.second != expectedSize) throw FileNotFoundException("MediaStore size does not match source bytes")
            Files.move(part.toPath(), destination.toPath(), StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING)
            return result
        } catch (error: Throwable) {
            part.delete()
            destination.delete()
            throw error
        }
    }

    private suspend fun hashAndSize(file: File, ensureActive: suspend () -> Unit): Pair<String, Long> {
        val digest = MessageDigest.getInstance("SHA-256")
        val coroutineContext = currentCoroutineContext()
        var size = 0L
        var bytesSincePhaseCheck = 0L
        file.inputStream().use { input ->
            val buffer = ByteArray(BUFFER_BYTES)
            while (true) {
                coroutineContext.ensureActive()
                if (bytesSincePhaseCheck >= PHASE_CHECK_INTERVAL_BYTES) {
                    ensureActive()
                    bytesSincePhaseCheck = 0L
                }
                val read = input.read(buffer)
                if (read < 0) break
                digest.update(buffer, 0, read)
                size += read
                bytesSincePhaseCheck += read
            }
        }
        return digest.digest().joinToString("") { "%02x".format(it) } to size
    }
}

internal class BackupMediaChangedException(val currentMedia: DeviceMedia? = null) : java.io.IOException("Медиа изменилось во время подготовки")

internal class BackupSourceReadException(cause: java.io.IOException) : FileNotFoundException("MediaStore source could not be read") {
    init { initCause(cause) }
}
