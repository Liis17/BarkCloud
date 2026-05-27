package com.barkfluff.BarkCloud.ui.components

import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import coil3.compose.AsyncImage
import coil3.request.ImageRequest
import coil3.request.crossfade
import com.barkfluff.BarkCloud.grpc.GrpcEndpoint

/**
 * Загрузка удалённой картинки по URL через Coil + trust-all OkHttp (см.
 * `BarkCloudApplication`). [normalize] пересобирает ссылку скачивания на актуальный
 * хост (нужно для аватара и прочих сохранённых в БД ссылок — см. [GrpcEndpoint]).
 */
@Composable
fun RemoteImage(
    url: String?,
    contentDescription: String?,
    modifier: Modifier = Modifier,
    contentScale: ContentScale = ContentScale.Crop,
    normalize: Boolean = false,
) {
    val resolved = if (normalize) GrpcEndpoint.normalizedFileDownloadURL(url) else url
    AsyncImage(
        model = ImageRequest.Builder(LocalContext.current)
            .data(resolved)
            .crossfade(true)
            .build(),
        contentDescription = contentDescription,
        modifier = modifier,
        contentScale = contentScale,
    )
}
