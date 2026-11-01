package com.barkfluff.BarkCloud.data.cache

import com.barkfluff.BarkCloud.data.cloud.CloudMediaOrigin
import com.barkfluff.BarkCloud.data.cloud.MediaAsset
import java.io.File

/**
 * Единая точка загрузки оригинала облачного файла для просмотра и действий.
 * Собственный файл использует own-ключ кеша и own `tempDownloadUrls`.
 * Общий файл сначала получает URL через shared RPC (авторизация всегда, даже при попадании в кеш),
 * затем использует отдельный shared-original ключ; own API не вызывается.
 */
internal suspend fun <T> withCloudOriginal(
    asset: MediaAsset,
    cache: FileCacheService,
    sharedDownloadUrl: suspend (fileId: String) -> String,
    action: suspend (File) -> T,
): T = when (asset.origin) {
    CloudMediaOrigin.OWN -> cache.withLoadedPinnedOriginal(asset.id, asset.fileName, action)
    CloudMediaOrigin.SHARED -> {
        val url = sharedDownloadUrl(asset.id)
        cache.withLoadedPinnedSharedOriginal(asset.id, asset.fileName, url, action)
    }
}

/** Сбрасывает оригинал того же ключа, который использовала [withCloudOriginal]. */
internal suspend fun FileCacheService.invalidateCloudOriginal(asset: MediaAsset) {
    when (asset.origin) {
        CloudMediaOrigin.OWN -> invalidateOriginal(asset.id)
        CloudMediaOrigin.SHARED -> invalidateSharedOriginal(asset.id)
    }
}

/**
 * JPEG-вид облачного фото. Для общего файла авторизация через shared RPC выполняется до любого чтения кеша:
 * отозванный доступ не должен отдать закешированную полноразмерную копию.
 */
internal suspend fun <T> withCloudJpegView(
    asset: MediaAsset,
    cache: FileCacheService,
    sharedDownloadUrl: suspend (fileId: String) -> String,
    model: CloudImageModel,
    action: suspend (File) -> T,
): T {
    if (asset.origin == CloudMediaOrigin.SHARED) sharedDownloadUrl(asset.id)
    return cache.withLoadedPinnedJpegView(asset.id, model.url, model.previewFileId, action)
}
