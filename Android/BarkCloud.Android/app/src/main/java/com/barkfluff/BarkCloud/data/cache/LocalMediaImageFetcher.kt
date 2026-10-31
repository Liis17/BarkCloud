package com.barkfluff.BarkCloud.data.cache

import coil3.ImageLoader
import coil3.decode.DataSource
import coil3.decode.ImageSource
import coil3.fetch.Fetcher
import coil3.fetch.FetchResult
import coil3.fetch.SourceFetchResult
import coil3.key.Keyer
import coil3.request.Options
import okio.Path.Companion.toOkioPath
import java.io.File

data class LocalMediaImageModel(
    val uri: String,
    val sourceId: String,
    val sourceVersion: String,
    val maxDimensionPx: Int,
    val isVideo: Boolean,
) {
    fun cacheKey(scope: CacheScope) =
        FileCacheKey.localPreview(scope, sourceId, sourceVersion, maxDimensionPx)
}

fun interface LocalMediaThumbnailLoader {
    suspend fun load(model: LocalMediaImageModel, destination: File)
}

class LocalMediaImageKeyer(
    private val currentScope: () -> CacheScope,
) : Keyer<LocalMediaImageModel> {
    override fun key(data: LocalMediaImageModel, options: Options): String =
        data.cacheKey(currentScope()).storageKey
}

class LocalMediaImageFetcherFactory(
    private val fileCache: FileCacheService,
    private val currentScope: () -> CacheScope,
    private val thumbnailLoader: LocalMediaThumbnailLoader,
) : Fetcher.Factory<LocalMediaImageModel> {
    override fun create(data: LocalMediaImageModel, options: Options, imageLoader: ImageLoader): Fetcher =
        LocalMediaImageFetcher(data, fileCache, currentScope, thumbnailLoader, options.fileSystem)
}

internal class LocalMediaImageFetcher(
    private val model: LocalMediaImageModel,
    private val fileCache: FileCacheService,
    private val currentScope: () -> CacheScope,
    private val thumbnailLoader: LocalMediaThumbnailLoader,
    private val fileSystem: okio.FileSystem,
) : Fetcher {
    override suspend fun fetch(): FetchResult {
        val file = fileCache.loadGenerated(
            key = model.cacheKey(currentScope()),
            fileName = "local-preview.webp",
        ) { destination -> thumbnailLoader.load(model, destination) }
        return SourceFetchResult(
            source = ImageSource(file.toOkioPath(), fileSystem),
            mimeType = "image/webp",
            dataSource = DataSource.DISK,
        )
    }
}
