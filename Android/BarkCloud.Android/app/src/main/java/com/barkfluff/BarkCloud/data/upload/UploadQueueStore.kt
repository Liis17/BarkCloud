package com.barkfluff.BarkCloud.data.upload

import android.content.Context
import android.net.Uri
import com.barkfluff.BarkCloud.net.queryFileName
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.util.UUID

class UploadQueueStore(private val context: Context) {
    private val prefs = context.getSharedPreferences("barkcloud_upload_queue", Context.MODE_PRIVATE)
    private val queueDir = File(context.filesDir, "upload_queue")

    suspend fun enqueue(
        uri: Uri,
        fileName: String = queryFileName(context, uri),
        directoryId: String? = null,
        albumId: String? = null,
    ): UploadQueueItem =
        withContext(Dispatchers.IO) {
            queueDir.mkdirs()
            val id = UUID.randomUUID().toString()
            val safeName = fileName.ifBlank { "file" }.replace(Regex("[\\\\/:*?\"<>|]"), "_")
            val dest = File(queueDir, "$id-$safeName")
            context.contentResolver.openInputStream(uri)?.use { input ->
                dest.outputStream().use { output -> input.copyTo(output) }
            } ?: error("Cannot open $uri")
            val item = UploadQueueItem(
                id = id,
                filePath = dest.absolutePath,
                fileName = fileName,
                directoryId = directoryId,
                albumId = albumId,
            )
            synchronized(this@UploadQueueStore) {
                save(readItemsLocked() + item)
            }
            item
        }

    fun pending(): List<UploadQueueItem> = synchronized(this) {
        readItemsLocked()
    }

    fun remove(id: String) = synchronized(this) {
        val items = readItemsLocked()
        val removed = items.firstOrNull { it.id == id }
        save(items.filterNot { it.id == id })
        removed?.let { File(it.filePath).delete() }
    }

    fun clear() = synchronized(this) {
        save(emptyList())
        queueDir.deleteRecursively()
        queueDir.mkdirs()
    }

    private fun readItemsLocked(): List<UploadQueueItem> {
        val raw = prefs.getString(KEY_ITEMS, "[]").orEmpty()
        val array = runCatching { JSONArray(raw) }.getOrElse { JSONArray() }
        return buildList {
            for (index in 0 until array.length()) {
                val obj = array.optJSONObject(index) ?: continue
                add(
                    UploadQueueItem(
                        id = obj.optString("id"),
                        filePath = obj.optString("filePath"),
                        fileName = obj.optString("fileName"),
                        directoryId = obj.optString("directoryId").ifBlank { null },
                        albumId = obj.optString("albumId").ifBlank { null },
                    )
                )
            }
        }.filter { it.id.isNotBlank() && it.filePath.isNotBlank() }
    }

    private fun save(items: List<UploadQueueItem>) {
        val array = JSONArray()
        items.forEach { item ->
            array.put(
                JSONObject()
                    .put("id", item.id)
                    .put("filePath", item.filePath)
                    .put("fileName", item.fileName)
                    .put("directoryId", item.directoryId.orEmpty())
                    .put("albumId", item.albumId.orEmpty())
            )
        }
        prefs.edit().putString(KEY_ITEMS, array.toString()).apply()
    }

    companion object {
        private const val KEY_ITEMS = "items"
    }
}

data class UploadQueueItem(
    val id: String,
    val filePath: String,
    val fileName: String,
    val directoryId: String? = null,
    val albumId: String? = null,
)
