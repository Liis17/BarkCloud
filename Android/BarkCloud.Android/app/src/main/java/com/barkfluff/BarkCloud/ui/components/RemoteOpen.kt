package com.barkfluff.BarkCloud.ui.components

import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.platform.LocalContext
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.lifecycleScope
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.files.data.FileShareHelper
import com.barkfluff.BarkCloud.files.data.MimeIcon
import kotlinx.coroutines.launch

/**
 * Возвращает функцию открытия облачного файла во внешнем приложении: временная ссылка
 * (`GetTempDownloadUrl`) → скачивание оригинала в кэш → `ACTION_VIEW` через FileProvider.
 * Используется для просмотра видео и документов из облака.
 */
@Composable
fun rememberRemoteOpener(): (fileId: String, fileName: String, onError: (String) -> Unit) -> Unit {
    val context = LocalContext.current
    val app = context.applicationContext as BarkCloudApplication
    val scope = LocalLifecycleOwner.current.lifecycleScope
    return remember(app) {
        { fileId, fileName, onError ->
            scope.launch {
                runCatching {
                    val file = app.fileCache.loadOriginal(fileId, fileName)
                    context.startActivity(FileShareHelper.buildOpenIntent(context, file, MimeIcon.mimeFor(fileName)))
                }.onFailure { onError(it.message ?: "open failed") }
            }
        }
    }
}
