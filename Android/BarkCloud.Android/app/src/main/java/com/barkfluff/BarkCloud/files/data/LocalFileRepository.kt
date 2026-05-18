package com.barkfluff.BarkCloud.files.data

import com.barkfluff.BarkCloud.files.domain.FsEntry
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.io.IOException

class LocalFileRepository {

    suspend fun list(dirPath: String, includeHidden: Boolean): Result<List<FsEntry>> =
        withContext(Dispatchers.IO) {
            runCatching {
                val dir = File(dirPath)
                if (!dir.exists() || !dir.isDirectory) {
                    throw IOException("Каталог недоступен: $dirPath")
                }
                val children = dir.listFiles().orEmpty()
                children
                    .asSequence()
                    .filter { includeHidden || !it.name.startsWith(".") }
                    .map { toEntry(it) }
                    .toList()
            }
        }

    private fun toEntry(file: File): FsEntry = if (file.isDirectory) {
        val count = file.list()?.size ?: 0
        FsEntry.Directory(
            path = file.absolutePath,
            name = file.name,
            lastModified = file.lastModified(),
            childCount = count,
        )
    } else {
        FsEntry.File(
            path = file.absolutePath,
            name = file.name,
            lastModified = file.lastModified(),
            sizeBytes = file.length(),
            mimeType = MimeIcon.mimeFor(file.name),
        )
    }

    suspend fun createDir(parentPath: String, name: String): Result<FsEntry.Directory> =
        withContext(Dispatchers.IO) {
            runCatching {
                require(name.isNotBlank()) { "Имя не может быть пустым" }
                require(!name.contains('/') && !name.contains('\\')) { "Недопустимое имя" }
                val parent = File(parentPath)
                val target = File(parent, name)
                if (target.exists()) error("Уже существует")
                if (!target.mkdir()) throw IOException("Не удалось создать папку")
                FsEntry.Directory(
                    path = target.absolutePath,
                    name = target.name,
                    lastModified = target.lastModified(),
                    childCount = 0,
                )
            }
        }

    suspend fun rename(entry: FsEntry, newName: String): Result<FsEntry> =
        withContext(Dispatchers.IO) {
            runCatching {
                require(newName.isNotBlank()) { "Имя не может быть пустым" }
                require(!newName.contains('/') && !newName.contains('\\')) { "Недопустимое имя" }
                val src = File(entry.path)
                val target = File(src.parentFile, newName)
                if (target.exists()) error("Уже существует")
                if (!src.renameTo(target)) throw IOException("Не удалось переименовать")
                toEntry(target)
            }
        }

    suspend fun delete(entries: List<FsEntry>): Result<Unit> =
        withContext(Dispatchers.IO) {
            runCatching {
                entries.forEach { entry ->
                    val file = File(entry.path)
                    if (file.exists() && !deleteRecursively(file)) {
                        throw IOException("Не удалось удалить ${entry.name}")
                    }
                }
            }
        }

    private fun deleteRecursively(file: File): Boolean {
        if (file.isDirectory) {
            file.listFiles()?.forEach { child ->
                if (!deleteRecursively(child)) return false
            }
        }
        return file.delete()
    }

    suspend fun copy(
        entries: List<FsEntry>,
        targetDirPath: String,
        onProgress: (Float) -> Unit,
    ): Result<Unit> = withContext(Dispatchers.IO) {
        runCatching {
            val targetDir = File(targetDirPath)
            require(targetDir.isDirectory) { "Целевая папка недоступна" }
            val totalBytes = entries.sumOf { sizeOf(File(it.path)) }.coerceAtLeast(1L)
            var done = 0L
            entries.forEach { entry ->
                val src = File(entry.path)
                val dst = uniqueChild(targetDir, src.name)
                copyRecursively(src, dst) { delta ->
                    done += delta
                    onProgress((done.toFloat() / totalBytes).coerceIn(0f, 1f))
                }
            }
            onProgress(1f)
        }
    }

    suspend fun move(
        entries: List<FsEntry>,
        targetDirPath: String,
        onProgress: (Float) -> Unit,
    ): Result<Unit> = withContext(Dispatchers.IO) {
        runCatching {
            val targetDir = File(targetDirPath)
            require(targetDir.isDirectory) { "Целевая папка недоступна" }
            // Запрещаем перемещать папку в саму себя или вглубь себя.
            entries.forEach { entry ->
                val src = File(entry.path)
                if (src.isDirectory && targetDir.absolutePath.startsWith(src.absolutePath + File.separator)) {
                    throw IOException("Нельзя переместить папку внутрь самой себя")
                }
            }
            val totalBytes = entries.sumOf { sizeOf(File(it.path)) }.coerceAtLeast(1L)
            var done = 0L
            entries.forEach { entry ->
                val src = File(entry.path)
                val dst = uniqueChild(targetDir, src.name)
                if (src.renameTo(dst)) {
                    done += sizeOf(dst)
                    onProgress((done.toFloat() / totalBytes).coerceIn(0f, 1f))
                } else {
                    copyRecursively(src, dst) { delta ->
                        done += delta
                        onProgress((done.toFloat() / totalBytes).coerceIn(0f, 1f))
                    }
                    if (!deleteRecursively(src)) throw IOException("Не удалось удалить ${src.name} после копирования")
                }
            }
            onProgress(1f)
        }
    }

    private fun sizeOf(file: File): Long {
        if (!file.exists()) return 0L
        if (file.isFile) return file.length()
        var total = 0L
        file.listFiles()?.forEach { total += sizeOf(it) }
        return total
    }

    private fun uniqueChild(parent: File, requestedName: String): File {
        val target = File(parent, requestedName)
        if (!target.exists()) return target
        val baseName = requestedName.substringBeforeLast('.', requestedName)
        val ext = requestedName.substringAfterLast('.', missingDelimiterValue = "")
        var i = 1
        while (true) {
            val candidateName = if (ext.isEmpty()) "${baseName}_$i" else "${baseName}_$i.$ext"
            val candidate = File(parent, candidateName)
            if (!candidate.exists()) return candidate
            i++
        }
    }

    private fun copyRecursively(src: File, dst: File, onBytes: (Long) -> Unit) {
        if (src.isDirectory) {
            if (!dst.mkdirs() && !dst.isDirectory) throw IOException("Не удалось создать ${dst.name}")
            src.listFiles()?.forEach { child ->
                copyRecursively(child, File(dst, child.name), onBytes)
            }
        } else {
            src.inputStream().use { input ->
                dst.outputStream().use { output ->
                    val buffer = ByteArray(64 * 1024)
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) break
                        output.write(buffer, 0, read)
                        onBytes(read.toLong())
                    }
                }
            }
        }
    }
}
