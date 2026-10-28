package com.barkfluff.BarkCloud.data.cache

import coil3.ImageLoader
import coil3.decode.DataSource
import coil3.decode.ImageSource
import coil3.disk.DiskCache
import coil3.fetch.Fetcher
import coil3.fetch.FetchResult
import coil3.fetch.SourceFetchResult
import coil3.key.Keyer
import coil3.request.Options
import okio.Path.Companion.toOkioPath
import java.io.File
import java.io.IOException

data class CloudImageModel(
    val url: String,
    val fileId: String? = null,
    val previewFileId: String? = null,
    val variant: CloudImageVariant = CloudImageVariant.PREVIEW,
)

enum class CloudImageVariant {
    PREVIEW,
    JPEG_VIEW,
}

class CloudImageKeyer(
    private val currentScope: () -> CacheScope,
) : Keyer<CloudImageModel> {
    override fun key(data: CloudImageModel, options: Options): String = data.cacheKey(currentScope()).storageKey
}

class CloudImageFetcherFactory(
    private val fileCache: FileCacheService,
    private val currentScope: () -> CacheScope,
) : Fetcher.Factory<CloudImageModel> {
    override fun create(data: CloudImageModel, options: Options, imageLoader: ImageLoader): Fetcher =
        CloudImageFetcher(
            model = data,
            fileCache = fileCache,
            currentScope = currentScope,
            legacyDiskCache = imageLoader.diskCache,
            fileSystem = options.fileSystem,
        )
}

internal class CloudImageFetcher(
    private val model: CloudImageModel,
    private val fileCache: FileCacheService,
    private val currentScope: () -> CacheScope,
    private val legacyDiskCache: DiskCache?,
    private val fileSystem: okio.FileSystem,
) : Fetcher {
    override suspend fun fetch(): FetchResult {
        val key = model.cacheKey(currentScope())
        val cached = fileCache.get(key)
        val (file, dataSource) = when {
            cached != null -> cached to DataSource.DISK
            else -> loadFile(key)
        }
        return SourceFetchResult(
            source = ImageSource(file.toOkioPath(), fileSystem),
            mimeType = "image/jpeg",
            dataSource = dataSource,
        )
    }

    private suspend fun loadFile(key: FileCacheKey): Pair<File, DataSource> {
        val snapshot = legacyDiskCache?.openSnapshot(model.url)
        if (snapshot != null) {
            try {
                try {
                    val file = fileCache.load(
                        key = key,
                        fileName = model.cacheFileName,
                        sourceFile = File(snapshot.data.toString()),
                    )
                    return file to DataSource.DISK
                } catch (_: IOException) {
                    // A stale legacy entry must not prevent downloading a usable preview.
                }
            } finally {
                snapshot.close()
            }
        }
        return fileCache.load(key, model.cacheFileName, sourceUrl = model.url) to DataSource.NETWORK
    }
}

private fun CloudImageModel.cacheKey(scope: CacheScope): FileCacheKey {
    val ownerFileId = fileId?.takeIf(String::isNotBlank) ?: "url:$url"
    return when (variant) {
        CloudImageVariant.PREVIEW -> FileCacheKey.preview(scope, ownerFileId, previewFileId, url)
        CloudImageVariant.JPEG_VIEW -> FileCacheKey.jpegView(scope, ownerFileId)
    }
}

private val CloudImageModel.cacheFileName: String
    get() = when (variant) {
        CloudImageVariant.PREVIEW -> "preview.jpg"
        CloudImageVariant.JPEG_VIEW -> "jpeg-view.jpg"
    }
