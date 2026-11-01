package com.barkfluff.BarkCloud.data.gallery

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject

/** Незавершённые облачные очистки удалённых фото; переживают перезапуск приложения (SharedPreferences). */
internal class PendingCloudCleanupStore(context: Context) : PendingCleanupRepository {
    private val prefs = context.applicationContext.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    override fun all(): List<PendingCloudCleanup> {
        val raw = prefs.getString(KEY_ITEMS, null) ?: return emptyList()
        val array = JSONArray(raw)
        return (0 until array.length()).map { index ->
            val item = array.getJSONObject(index)
            PendingCloudCleanup(item.getString("mediaKey"), item.getString("hash"))
        }
    }

    override fun add(entry: PendingCloudCleanup) {
        write(all().filterNot { it.mediaKey == entry.mediaKey } + entry)
    }

    override fun remove(mediaKey: String) {
        write(all().filterNot { it.mediaKey == mediaKey })
    }

    private fun write(items: List<PendingCloudCleanup>) {
        val array = JSONArray()
        items.forEach { item ->
            array.put(JSONObject().put("mediaKey", item.mediaKey).put("hash", item.hash))
        }
        prefs.edit().putString(KEY_ITEMS, array.toString()).apply()
    }

    private companion object {
        const val PREFS_NAME = "barkcloud_pending_cloud_cleanup"
        const val KEY_ITEMS = "items"
    }
}
