package com.barkfluff.BarkCloud.ui.selection

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.widget.Toast
import androidx.compose.runtime.Composable
import androidx.compose.runtime.Stable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.LocalContext
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cache.LocalShareFileCache
import com.barkfluff.BarkCloud.data.cache.withCloudOriginal
import com.barkfluff.BarkCloud.data.cloud.MediaAsset
import com.barkfluff.BarkCloud.data.gallery.DeviceMedia
import com.barkfluff.BarkCloud.files.data.FileShareHelper
import com.barkfluff.BarkCloud.files.data.MimeIcon
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * Отправка выбранных медиа через системный лист «Поделиться».
 * Облачные оригиналы скачиваются в файловый кеш и копируются в каталог шаринга
 * (как одиночное «Поделиться» в просмотрщике); фото устройства передаются своими content URI.
 * Повторное нажатие во время подготовки отменяет её.
 */
@Stable
internal class SelectionSharer(
    private val context: Context,
    private val app: BarkCloudApplication,
    private val scope: CoroutineScope,
) {
    private var job: Job? by mutableStateOf(null)

    val isPreparing: Boolean get() = job != null

    fun share(cloud: List<MediaAsset>, device: List<DeviceMedia> = emptyList()) {
        job?.let { it.cancel(); return }
        if (cloud.isEmpty() && device.isEmpty()) return
        val launched = scope.launch(start = CoroutineStart.LAZY) {
            try {
                val items = device.map { it.uri to (it.mimeType ?: if (it.isVideo) "video/*" else "image/*") }.toMutableList()
                var failed = 0
                cloud.forEach { asset ->
                    try {
                        val copy = withCloudOriginal(asset, app.fileCache, { fileId -> app.sharedRepository.getSharedFileDownloadUrl(fileId) }) { original ->
                            withContext(Dispatchers.IO) {
                                LocalShareFileCache.prepare(context.cacheDir, original, displayName = asset.fileName)
                            }
                        }
                        items += FileShareHelper.uriFor(context, copy) to MimeIcon.mimeFor(asset.fileName)
                    } catch (error: CancellationException) {
                        throw error
                    } catch (_: Exception) {
                        failed++
                    }
                }
                if (failed > 0) {
                    val total = cloud.size + device.size
                    toast(if (items.isEmpty()) context.getString(R.string.viewer_action_failed) else context.getString(R.string.selection_share_partial, failed, total))
                }
                if (items.isNotEmpty()) {
                    val chooser = Intent.createChooser(buildShareIntent(items), context.getString(R.string.viewer_action_share_title))
                    runCatching { context.startActivity(chooser) }.onFailure { toast(context.getString(R.string.viewer_action_failed)) }
                }
            } finally {
                if (job === coroutineContext[Job]) job = null
            }
        }
        job = launched
        launched.start()
    }

    private fun toast(message: String) = Toast.makeText(context, message, Toast.LENGTH_SHORT).show()

    private fun buildShareIntent(items: List<Pair<Uri, String>>): Intent {
        val mime = shareMimeType(items.map { it.second })
        val intent = if (items.size == 1) {
            Intent(Intent.ACTION_SEND).putExtra(Intent.EXTRA_STREAM, items.single().first)
        } else {
            Intent(Intent.ACTION_SEND_MULTIPLE).putParcelableArrayListExtra(Intent.EXTRA_STREAM, ArrayList(items.map { it.first }))
        }
        return intent.setType(mime).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
    }
}

@Composable
internal fun rememberSelectionSharer(): SelectionSharer {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    return remember(context, scope) {
        SelectionSharer(context, context.applicationContext as BarkCloudApplication, scope)
    }
}

// Общий MIME пакета: точный при совпадении, "image/*" и т.п. при общей группе, иначе "*/*".
internal fun shareMimeType(mimes: List<String>): String {
    val distinct = mimes.map { it.lowercase() }.distinct()
    if (distinct.size == 1) return distinct.single()
    val groups = distinct.map { it.substringBefore('/') }.distinct()
    return if (groups.size == 1 && groups.single() != "*") "${groups.single()}/*" else "*/*"
}
