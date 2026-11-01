package com.barkfluff.BarkCloud.data.cloud

import com.barkfluff.BarkCloud.data.cache.FileCacheService
import com.barkfluff.BarkCloud.data.cache.FileCachePin
import com.barkfluff.BarkCloud.net.FileTransferService
import java.io.IOException

/** Что отдать плееру: удалённый поток (временная или общая ссылка) либо локальный файл или URI. */
sealed interface PlaybackSource {
    data class Remote(val url: String) : PlaybackSource
    data class Local(val uri: String) : PlaybackSource
}

/** Источник плюс освобождение: снимает пин кеша, если воспроизведение идёт из кеша. */
class ResolvedPlayback(val source: PlaybackSource, val release: suspend () -> Unit)

/**
 * Выбирает источник облачного видео без полной загрузки.
 * Общий файл сначала запрашивает свежую ссылку через shared RPC (авторизация всегда, даже при кеше).
 * Собственный файл сначала проверяет кеш, затем берёт `tempDownloadUrls`.
 * Кешированный оригинал воспроизводится по пину; без кеша играется потоковая ссылка.
 *
 * Владение пином передаётся через [FileCacheService.pinCachedOriginal]: если вызов отменён между захватом
 * пина и возвратом, пин снимается здесь, в `finally`, иначе он остался бы в кеше навсегда.
 */
class VideoPlaybackResolver(
    private val fileCache: FileCacheService,
    private val fileTransfer: FileTransferService,
    private val sharedDownloadUrl: suspend (fileId: String) -> String,
) {
    suspend fun resolve(asset: MediaAsset): ResolvedPlayback {
        val shared = asset.origin == CloudMediaOrigin.SHARED
        val sharedUrl = if (shared) sharedDownloadUrl(asset.id) else null
        var acquired: FileCachePin? = null
        try {
            val pinned = fileCache.pinCachedOriginal(asset.id, shared) { acquired = it }
            if (pinned != null) {
                // Владение переходит к возвращаемому ResolvedPlayback; дальше без точек приостановки.
                acquired = null
                return ResolvedPlayback(PlaybackSource.Local(pinned.file.toURI().toString())) {
                    fileCache.unpin(pinned.pin)
                }
            }
        } finally {
            acquired?.let { fileCache.unpin(it) }
        }
        val url = sharedUrl
            ?: fileTransfer.tempDownloadUrls(listOf(asset.id))[asset.id]
            ?: throw IOException("Ссылка воспроизведения не найдена")
        return ResolvedPlayback(PlaybackSource.Remote(url)) {}
    }
}
