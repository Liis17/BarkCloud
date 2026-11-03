package com.barkfluff.BarkCloud.widgets

import android.appwidget.AppWidgetManager
import android.content.ComponentName
import android.content.Context
import android.graphics.Bitmap
import coil3.SingletonImageLoader
import coil3.request.CachePolicy
import coil3.request.ImageRequest
import coil3.request.SuccessResult
import coil3.toBitmap
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.data.cache.CloudImageModel
import com.barkfluff.BarkCloud.data.cloud.CloudMediaKind
import kotlinx.coroutines.CancellationException
import java.io.File
import java.io.FileOutputStream

/**
 * Локальное хранилище фото-виджета: выбранный альбом, список кадров (JPEG, уменьшенные)
 * и индекс текущего кадра. Кадры скачиваются через Coil приложения (с авторизацией) и
 * лежат в `filesDir`, поэтому ротация не требует сети и не зависит от процесса приложения.
 */
object PhotoWidgetStore {
    private const val PREFS_NAME = "barkcloud_photo_widget"
    private const val ROOT_DIR = "photo_widgets"
    private const val FRAME_COUNT = 12
    private const val FRAME_SOURCE_LIMIT = 30
    private const val FRAME_PX = 400
    private const val STALE_AFTER_MILLIS = 6 * 60 * 60 * 1000L

    data class Config(val albumId: String, val albumName: String)

    fun saveAlbum(context: Context, widgetId: Int, albumId: String, albumName: String) {
        // Кадры прежнего альбома не должны показываться с подписью нового до окончания загрузки.
        dir(context, widgetId).deleteRecursively()
        prefs(context).edit()
            .putString(key(widgetId, "album_id"), albumId)
            .putString(key(widgetId, "album_name"), albumName)
            .putLong(key(widgetId, "index"), 0L)
            .putLong(key(widgetId, "fetched_at"), 0L)
            .apply()
    }

    fun config(context: Context, widgetId: Int): Config? {
        val prefs = prefs(context)
        val albumId = prefs.getString(key(widgetId, "album_id"), null) ?: return null
        return Config(albumId, prefs.getString(key(widgetId, "album_name"), "").orEmpty())
    }

    fun hasFetched(context: Context, widgetId: Int): Boolean =
        prefs(context).getLong(key(widgetId, "fetched_at"), 0L) > 0L

    fun currentFrame(context: Context, widgetId: Int): File? {
        val frames = frames(context, widgetId)
        if (frames.isEmpty()) return null
        val index = prefs(context).getLong(key(widgetId, "index"), 0L).toInt()
        return frames[index.mod(frames.size)]
    }

    fun remove(context: Context, widgetId: Int) {
        val editor = prefs(context).edit()
        listOf("album_id", "album_name", "index", "fetched_at").forEach { editor.remove(key(widgetId, it)) }
        editor.apply()
        dir(context, widgetId).deleteRecursively()
    }

    fun clearAll(context: Context) {
        prefs(context).edit().clear().apply()
        File(context.filesDir, ROOT_DIR).deleteRecursively()
    }

    /** Фоновый проход: для каждого виджета с альбомом — переключить кадр или перекачать набор. */
    suspend fun refreshAll(app: BarkCloudApplication) {
        val context = app.applicationContext
        val manager = AppWidgetManager.getInstance(context)
        val ids = manager.getAppWidgetIds(ComponentName(context, PhotoWidgetProvider::class.java))
        for (id in ids) {
            val config = config(context, id) ?: continue
            if (needsFetch(context, id)) fetchFrames(app, id, config) else advance(context, id)
        }
        PhotoWidgetProvider.refreshAll(context)
    }

    private fun needsFetch(context: Context, widgetId: Int): Boolean {
        val fetchedAt = prefs(context).getLong(key(widgetId, "fetched_at"), 0L)
        return fetchedAt == 0L || System.currentTimeMillis() - fetchedAt > STALE_AFTER_MILLIS
    }

    /**
     * Качает кадры в staging-папку и подменяет ими рабочую только при успехе,
     * чтобы сетевой сбой не стирал уже показанные фото.
     */
    private suspend fun fetchFrames(app: BarkCloudApplication, widgetId: Int, config: Config) {
        val context = app.applicationContext
        val models: List<CloudImageModel> = try {
            app.albumRepository.listItems(config.albumId, kindFilter = CloudMediaKind.PHOTO, limit = FRAME_SOURCE_LIMIT)
                .items
                .mapNotNull { asset -> asset.previewModel(FRAME_PX) ?: asset.jpegViewModel() }
                .take(FRAME_COUNT)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            return
        }

        val staging = File(File(context.filesDir, ROOT_DIR), "$widgetId.staging").apply {
            deleteRecursively()
            mkdirs()
        }
        var failed = false
        models.forEachIndexed { index, model ->
            val bitmap = try {
                loadBitmap(context, model)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                null
            }
            if (bitmap == null) {
                failed = true
            } else {
                FileOutputStream(File(staging, "%02d.jpg".format(index))).use {
                    fitInto(bitmap, FRAME_PX).compress(Bitmap.CompressFormat.JPEG, 85, it)
                }
            }
        }
        if (failed && frames(context, widgetId).isNotEmpty()) {
            staging.deleteRecursively()
            return
        }

        val target = dir(context, widgetId)
        target.deleteRecursively()
        staging.renameTo(target)
        prefs(context).edit()
            .putLong(key(widgetId, "index"), 0L)
            .putLong(key(widgetId, "fetched_at"), System.currentTimeMillis())
            .apply()
    }

    /**
     * Битмап не освобождаем вручную: Coil может отдать тот же объект из общего memory cache,
     * и галерея упадёт на уже освобождённом битмапе. Memory cache для кадров отключён,
     * чтобы кадры виджета не вытесняли кеш галереи.
     */
    private suspend fun loadBitmap(context: Context, model: CloudImageModel): Bitmap? {
        val request = ImageRequest.Builder(context)
            .data(model)
            .size(FRAME_PX, FRAME_PX)
            .memoryCachePolicy(CachePolicy.DISABLED)
            .build()
        val result = SingletonImageLoader.get(context).execute(request)
        return (result as? SuccessResult)?.image?.toBitmap()
    }

    /** Coil может вернуть изображение больше запрошенного (субсэмплинг степенями двойки). */
    private fun fitInto(bitmap: Bitmap, maxPx: Int): Bitmap {
        val largest = maxOf(bitmap.width, bitmap.height)
        if (largest <= maxPx) return bitmap
        val scale = maxPx.toFloat() / largest
        return Bitmap.createScaledBitmap(
            bitmap,
            (bitmap.width * scale).toInt().coerceAtLeast(1),
            (bitmap.height * scale).toInt().coerceAtLeast(1),
            true,
        )
    }

    private fun advance(context: Context, widgetId: Int) {
        val size = frames(context, widgetId).size
        if (size == 0) return
        val prefs = prefs(context)
        val next = (prefs.getLong(key(widgetId, "index"), 0L) + 1) % size
        prefs.edit().putLong(key(widgetId, "index"), next).apply()
    }

    private fun frames(context: Context, widgetId: Int): List<File> =
        dir(context, widgetId).listFiles { file -> file.extension == "jpg" }?.sortedBy { it.name }.orEmpty()

    private fun dir(context: Context, widgetId: Int): File = File(File(context.filesDir, ROOT_DIR), widgetId.toString())

    private fun prefs(context: Context) = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    private fun key(widgetId: Int, name: String) = "$widgetId.$name"
}
