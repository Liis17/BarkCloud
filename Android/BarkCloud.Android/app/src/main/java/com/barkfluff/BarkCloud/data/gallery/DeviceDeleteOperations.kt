package com.barkfluff.BarkCloud.data.gallery

import com.barkfluff.BarkCloud.data.cache.CacheScope
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.nio.file.Files
import java.nio.file.StandardCopyOption

/**
 * Фазы durable-операции удаления фото устройства.
 * [AWAITING_SYSTEM] — операция записана до системного диалога; одобрения нет, облако не трогаем.
 * [APPROVED] — система подтвердила удаление локальных копий; облачные шаги идут по подтверждённым ID.
 */
internal enum class DeleteOpPhase { AWAITING_SYSTEM, APPROVED }

/**
 * Фото операции. [confirmedIds] — облачные копии, которые пользователь подтвердил. Список не расширяется:
 * новые копии, найденные позже, требуют нового подтверждения. [hiddenIds] — подтверждённые ID, которые
 * на момент подтверждения были в Vault. [doneIds] — ID, удаление которых подтверждено сервером или сверкой.
 */
internal data class OpPhoto(
    val mediaKey: String,
    val hash: String,
    val confirmedIds: List<String>,
    val hiddenIds: List<String>,
    val doneIds: List<String>,
) {
    val remainingIds: List<String> get() = confirmedIds.filterNot { it in doneIds }
}

internal data class DeleteOperation(
    val id: String,
    /** Канонический сервер и аккаунт, для которых подтверждена операция. */
    val scope: CacheScope,
    val mode: DeviceDeleteMode,
    val phase: DeleteOpPhase,
    val devicePhotos: List<String>,
    val photos: List<OpPhoto>,
) {
    val remainingCount: Int get() = photos.sumOf { it.remainingIds.size }
    val hasHiddenRemaining: Boolean get() = photos.any { photo -> photo.remainingIds.any { it in photo.hiddenIds } }
}

/**
 * Хранилище операций: один JSON-файл в приватной папке приложения, запись атомарна (временный файл и
 * перемещение). Повреждённый файл не удаляется молча: переносится в `*.corrupt`.
 */
internal class DeviceDeleteOperationStore(private val file: File) {
    private val lock = Any()

    fun all(): List<DeleteOperation> = synchronized(lock) { read() }

    fun get(id: String): DeleteOperation? = all().firstOrNull { it.id == id }

    fun put(operation: DeleteOperation) = synchronized(lock) {
        write(read().filterNot { it.id == operation.id } + operation)
    }

    fun remove(id: String) = synchronized(lock) {
        write(read().filterNot { it.id == id })
    }

    private fun read(): List<DeleteOperation> {
        if (!file.exists()) return emptyList()
        return try {
            val array = JSONArray(file.readText())
            (0 until array.length()).map { decode(array.getJSONObject(it)) }
        } catch (_: Exception) {
            val corrupt = File(file.parentFile, file.name + ".corrupt")
            runCatching { Files.move(file.toPath(), corrupt.toPath(), StandardCopyOption.REPLACE_EXISTING) }
            emptyList()
        }
    }

    private fun write(operations: List<DeleteOperation>) {
        file.parentFile?.mkdirs()
        val temp = File(file.parentFile, file.name + ".tmp")
        val array = JSONArray()
        operations.forEach { array.put(encode(it)) }
        temp.writeText(array.toString())
        Files.move(temp.toPath(), file.toPath(), StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE)
    }

    private fun encode(op: DeleteOperation): JSONObject = JSONObject()
        .put("id", op.id)
        .put("serverKey", op.scope.serverKey)
        .put("accountId", op.scope.accountId)
        .put("mode", op.mode.name)
        .put("phase", op.phase.name)
        .put("devicePhotos", JSONArray(op.devicePhotos))
        .put(
            "photos",
            JSONArray().also { photos ->
                op.photos.forEach { photo ->
                    photos.put(
                        JSONObject()
                            .put("mediaKey", photo.mediaKey)
                            .put("hash", photo.hash)
                            .put("confirmedIds", JSONArray(photo.confirmedIds))
                            .put("hiddenIds", JSONArray(photo.hiddenIds))
                            .put("doneIds", JSONArray(photo.doneIds)),
                    )
                }
            },
        )

    private fun decode(json: JSONObject): DeleteOperation = DeleteOperation(
        id = json.getString("id"),
        scope = CacheScope(json.getString("serverKey"), json.getString("accountId")),
        mode = DeviceDeleteMode.valueOf(json.getString("mode")),
        phase = DeleteOpPhase.valueOf(json.getString("phase")),
        devicePhotos = json.getJSONArray("devicePhotos").strings(),
        photos = json.getJSONArray("photos").let { array ->
            (0 until array.length()).map { index ->
                val photo = array.getJSONObject(index)
                OpPhoto(
                    mediaKey = photo.getString("mediaKey"),
                    hash = photo.getString("hash"),
                    confirmedIds = photo.getJSONArray("confirmedIds").strings(),
                    hiddenIds = photo.getJSONArray("hiddenIds").strings(),
                    doneIds = photo.getJSONArray("doneIds").strings(),
                )
            }
        },
    )

    private fun JSONArray.strings(): List<String> = (0 until length()).map { getString(it) }
}

/**
 * Разблокировка скрытых (Vault) целей для удаления фото устройства. Хранится только в памяти процесса:
 * сбрасывается при уходе с экрана и не записывается на диск, поэтому не становится постоянной авторизацией.
 */
internal object HiddenDeviceSession {
    @Volatile
    var unlocked: Boolean = false
}
