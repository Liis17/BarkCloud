package com.barkfluff.BarkCloud.data.vault

import android.content.Context
import com.barkfluff.BarkCloud.data.cloud.CloudMediaKind
import com.barkfluff.BarkCloud.data.cloud.MediaAsset
import com.barkfluff.BarkCloud.data.cloud.MediaPreview
import org.json.JSONArray
import org.json.JSONObject

/** Ссылка на облачный файл, спрятанная в vault (зеркалит iOS VaultItem). */
data class VaultItem(
    val fileId: String,
    val fileName: String,
    val previewUrl: String?,
    val previewWidth: Int,
    val isVideo: Boolean,
) {
    fun toMediaAsset(): MediaAsset = MediaAsset(
        id = fileId,
        fileName = fileName,
        fileSize = 0L,
        kind = if (isVideo) CloudMediaKind.VIDEO else CloudMediaKind.PHOTO,
        previews = previewUrl?.let { listOf(MediaPreview(it, previewWidth)) }.orEmpty(),
        createdAtMillis = 0L,
    )

    companion object {
        fun from(asset: MediaAsset): VaultItem {
            val preview = asset.previews.maxByOrNull { it.width }
            return VaultItem(
                fileId = asset.id,
                fileName = asset.fileName,
                previewUrl = preview?.url,
                previewWidth = preview?.width ?: 0,
                isVideo = asset.isVideo,
            )
        }
    }
}

/**
 * Локальный список файлов в vault — **без** Keystore-шифрования (обычный
 * SharedPreferences с JSON), зеркалит iOS UserDefaults-выбор. Vault защищает
 * от чужого взгляда, не от компрометации устройства — сервер ничего не знает
 * о "приватности" этих файлов, они остаются обычными облачными файлами.
 */
class VaultStore(context: Context) {
    private val prefs = context.applicationContext.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    fun items(): List<VaultItem> {
        val raw = prefs.getString(KEY_ITEMS, null) ?: return emptyList()
        val array = JSONArray(raw)
        return (0 until array.length()).map { i ->
            val o = array.getJSONObject(i)
            VaultItem(
                fileId = o.getString("fileId"),
                fileName = o.getString("fileName"),
                previewUrl = o.optString("previewUrl", "").ifEmpty { null },
                previewWidth = o.optInt("previewWidth", 0),
                isVideo = o.optBoolean("isVideo", false),
            )
        }
    }

    fun contains(fileId: String): Boolean = items().any { it.fileId == fileId }

    val isEmpty: Boolean get() = items().isEmpty()

    fun add(item: VaultItem) {
        if (contains(item.fileId)) return
        write(items() + item)
    }

    fun remove(fileId: String) {
        write(items().filterNot { it.fileId == fileId })
    }

    fun removeAll() {
        prefs.edit().remove(KEY_ITEMS).apply()
    }

    private fun write(items: List<VaultItem>) {
        val array = JSONArray()
        items.forEach { item ->
            array.put(
                JSONObject()
                    .put("fileId", item.fileId)
                    .put("fileName", item.fileName)
                    .put("previewUrl", item.previewUrl.orEmpty())
                    .put("previewWidth", item.previewWidth)
                    .put("isVideo", item.isVideo),
            )
        }
        prefs.edit().putString(KEY_ITEMS, array.toString()).apply()
    }

    private companion object {
        const val PREFS_NAME = "barkcloud_vault"
        const val KEY_ITEMS = "items"
    }
}
