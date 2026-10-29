package com.barkfluff.BarkCloud.ui.components

import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.AnimatedVisibilityScope
import androidx.compose.animation.Crossfade
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.animation.core.MutableTransitionState
import androidx.compose.animation.core.animate
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectVerticalDragGestures
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.PlayCircle
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LoadingIndicator
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.zIndex
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import coil3.compose.AsyncImage
import coil3.request.ImageRequest
import me.saket.telephoto.zoomable.ZoomableImageState
import me.saket.telephoto.zoomable.coil3.ZoomableAsyncImage
import me.saket.telephoto.zoomable.rememberZoomableImageState
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.cache.CacheInvalidatedException
import com.barkfluff.BarkCloud.data.cache.CloudImageModel
import com.barkfluff.BarkCloud.data.cloud.CloudMediaKind
import com.barkfluff.BarkCloud.data.cloud.MediaAsset
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.launch
import java.io.File
import java.io.IOException
import java.text.DateFormat
import java.util.Date

private val DismissDistance = 120.dp
private val SheetOpenDistance = 80.dp

private sealed interface ViewerImageState {
    data object Preview : ViewerImageState
    data object Loading : ViewerImageState
    data class Original(val file: File) : ViewerImageState
    data class JpegView(val model: CloudImageModel) : ViewerImageState
    data object Error : ViewerImageState
}

/** Полноэкранный просмотрщик остаётся в общем Compose-слое с сеткой для shared-element перехода. */
@Composable
fun CloudMediaViewer(
    assets: List<MediaAsset>,
    selectedAsset: MediaAsset?,
    onDismiss: () -> Unit,
    onPlayVideo: (MediaAsset) -> Unit,
    infoEnabled: Boolean = true,
    onDelete: (suspend (MediaAsset) -> Unit)? = null,
) {
    val visibility = remember { MutableTransitionState(false) }
    var retainedAsset by remember { mutableStateOf<MediaAsset?>(null) }

    LaunchedEffect(selectedAsset) {
        if (selectedAsset != null) retainedAsset = selectedAsset
        visibility.targetState = selectedAsset != null
    }
    LaunchedEffect(visibility.isIdle, visibility.currentState) {
        if (visibility.isIdle && !visibility.currentState) retainedAsset = null
    }

    AnimatedVisibility(
        visibleState = visibility,
        modifier = Modifier.fillMaxSize().zIndex(1f),
        enter = fadeIn(tween(180)),
        exit = fadeOut(tween(180)),
    ) {
        val asset = retainedAsset ?: selectedAsset
        if (asset != null) {
            val viewerAssets = if (assets.isEmpty()) listOf(asset) else assets
            val initialIndex = viewerAssets.indexOfFirst { it.id == asset.id }.coerceAtLeast(0)
            key(asset.id) {
                CloudMediaViewerContent(
                    assets = viewerAssets,
                    initialIndex = initialIndex,
                    isOpen = selectedAsset?.id == asset.id,
                    onDismiss = onDismiss,
                    onPlayVideo = onPlayVideo,
                    infoEnabled = infoEnabled,
                    onDelete = onDelete,
                    animatedVisibilityScope = this@AnimatedVisibility,
                )
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun CloudMediaViewerContent(
    assets: List<MediaAsset>,
    initialIndex: Int,
    isOpen: Boolean,
    onDismiss: () -> Unit,
    onPlayVideo: (MediaAsset) -> Unit,
    infoEnabled: Boolean,
    onDelete: (suspend (MediaAsset) -> Unit)?,
    animatedVisibilityScope: AnimatedVisibilityScope,
) {
    val context = LocalContext.current
    val app = context.applicationContext as BarkCloudApplication
    val pagerState = rememberPagerState(initialPage = initialIndex) { assets.size }
    val scope = rememberCoroutineScope()
    val dismissPx = with(LocalDensity.current) { DismissDistance.toPx() }
    val sheetOpenPx = with(LocalDensity.current) { SheetOpenDistance.toPx() }
    var dragY by remember { mutableFloatStateOf(0f) }
    val backdropAlpha = 1f - (dragY / dismissPx).coerceIn(0f, 1f) * 0.8f
    var sheetVisible by remember { mutableStateOf(false) }
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = false)
    val infoViewModel: CloudMediaInfoViewModel = viewModel(factory = CloudMediaInfoViewModel.factory())
    val currentAsset = assets.getOrNull(pagerState.currentPage) ?: return
    val dateTimeText = remember(currentAsset.createdAtMillis) {
        currentAsset.createdAtMillis.takeIf { it > 0L }?.let {
            DateFormat.getDateTimeInstance().format(Date(it))
        }.orEmpty()
    }
    var overlayVisible by remember { mutableStateOf(true) }
    val zoomStates = remember { mutableMapOf<String, ZoomableImageState>() }
    val activeZoomState = remember { mutableStateOf<ZoomableImageState?>(null) }
    val isZoomed by remember {
        derivedStateOf {
            (activeZoomState.value?.zoomableState?.contentTransformation?.scaleMetadata?.userZoom ?: 1f) > 1.01f
        }
    }
    var previousSettledAssetId by remember {
        mutableStateOf(assets.getOrNull(initialIndex)?.id)
    }
    val infoState by infoViewModel.state.collectAsStateWithLifecycle()
    val viewerAssetId = LocalCloudMediaViewerAssetId.current
    val jpegViewModel = currentAsset.jpegViewModel()
    var useJpegView by remember(currentAsset.id) {
        mutableStateOf(requiresJpegView(currentAsset.fileName, jpegViewModel != null))
    }
    var retryCount by remember(currentAsset.id) { mutableIntStateOf(0) }
    var imageState by remember(currentAsset.id) { mutableStateOf<ViewerImageState>(ViewerImageState.Preview) }
    var failedDecodeCache by remember(currentAsset.id) { mutableStateOf<ViewerCacheKind?>(null) }

    LaunchedEffect(pagerState.settledPage) {
        val settledAsset = assets.getOrNull(pagerState.settledPage) ?: return@LaunchedEffect
        val previousAssetId = previousSettledAssetId
        if (previousAssetId != null && previousAssetId != settledAsset.id) {
            zoomStates.remove(previousAssetId)?.zoomableState?.resetZoom()
        }
        previousSettledAssetId = settledAsset.id
    }

    BackHandler(enabled = isOpen, onBack = onDismiss)

    LaunchedEffect(isOpen, currentAsset.id, retryCount, useJpegView) {
        if (!isOpen || currentAsset.kind != CloudMediaKind.PHOTO) return@LaunchedEffect
        try {
            if (useJpegView) {
                val model = jpegViewModel ?: run {
                    imageState = ViewerImageState.Error
                    return@LaunchedEffect
                }
                imageState = ViewerImageState.Loading
                app.fileCache.withLoadedPinnedJpegView(currentAsset.id, model.url, model.previewFileId) {
                    imageState = ViewerImageState.JpegView(model)
                    awaitCancellation()
                }
            } else {
                imageState = ViewerImageState.Loading
                app.fileCache.withLoadedPinnedOriginal(currentAsset.id, currentAsset.fileName) { file ->
                    imageState = ViewerImageState.Original(file)
                    awaitCancellation()
                }
            }
        } catch (error: CancellationException) {
            throw error
        } catch (_: CacheInvalidatedException) {
            imageState = ViewerImageState.Error
        } catch (_: IOException) {
            imageState = ViewerImageState.Error
        } catch (_: Exception) {
            imageState = ViewerImageState.Error
        }
    }

    LaunchedEffect(sheetVisible, currentAsset.id, isOpen) {
        if (sheetVisible && isOpen) infoViewModel.load(currentAsset.id)
        if (!isOpen) sheetVisible = false
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Color.Black.copy(alpha = backdropAlpha))
            .graphicsLayer { translationY = dragY }
            .pointerInput(dismissPx, sheetOpenPx, isZoomed) {
                if (isZoomed) return@pointerInput
                var movedUp = 0f
                detectVerticalDragGestures(
                    onDragStart = { movedUp = 0f },
                    onDragEnd = {
                        when {
                            dragY > dismissPx -> onDismiss()
                            infoEnabled && movedUp < -sheetOpenPx -> {
                                sheetVisible = true
                                scope.launch { animate(dragY, 0f) { value, _ -> dragY = value } }
                            }
                            else -> scope.launch { animate(dragY, 0f) { value, _ -> dragY = value } }
                        }
                    },
                    onDragCancel = {
                        scope.launch { animate(dragY, 0f) { value, _ -> dragY = value } }
                    },
                ) { _, amount ->
                    movedUp += amount
                    dragY = (dragY + amount).coerceAtLeast(0f)
                }
            },
    ) {
        HorizontalPager(
            state = pagerState,
            key = { page -> assets[page].id },
            modifier = Modifier.fillMaxSize(),
        ) { page ->
            val asset = assets[page]
            val isCurrentPage = page == pagerState.currentPage
            val zoomableImageState = rememberZoomableImageState()
            SideEffect {
                zoomStates[asset.id] = zoomableImageState
                if (isCurrentPage) activeZoomState.value = zoomableImageState
            }
            val pageImageState = if (isCurrentPage) imageState else ViewerImageState.Preview
            val previewModifier = if (page == initialIndex) Modifier.fillMaxSize().cloudMediaSharedElement(
                key = asset.id,
                visible = viewerAssetId == asset.id && isCurrentPage,
                animatedVisibilityScope = animatedVisibilityScope,
            ) else Modifier.fillMaxSize()

            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                AsyncImage(
                    model = asset.previewModel(1024),
                    contentDescription = asset.fileName,
                    modifier = previewModifier.clickable { overlayVisible = !overlayVisible },
                    contentScale = ContentScale.Fit,
                )
                if (isCurrentPage && asset.kind == CloudMediaKind.PHOTO) {
                    Crossfade(
                        targetState = pageImageState.takeIf {
                            it is ViewerImageState.Original || it is ViewerImageState.JpegView
                        },
                        animationSpec = tween(220),
                        label = "cloud-original",
                    ) { image ->
                        when (image) {
                            is ViewerImageState.Original -> {
                                val request = remember(image.file, asset.id, useJpegView) {
                                    ImageRequest.Builder(context)
                                        .data(image.file)
                                        .listener(onError = { _, _ ->
                                            if (!useJpegView && jpegViewModel != null) {
                                                failedDecodeCache = null
                                                scope.launch {
                                                    invalidateViewerCacheSafely {
                                                        app.fileCache.invalidateOriginal(asset.id)
                                                    }
                                                }
                                                imageState = ViewerImageState.Loading
                                                useJpegView = true
                                            } else {
                                                failedDecodeCache = viewerCacheKindAfterDecodeFailure(useJpegView)
                                                imageState = ViewerImageState.Error
                                            }
                                        })
                                        .build()
                                }
                                ZoomableAsyncImage(
                                    model = request,
                                    contentDescription = asset.fileName,
                                    modifier = Modifier.fillMaxSize(),
                                    state = zoomableImageState,
                                    contentScale = ContentScale.Fit,
                                    onClick = { overlayVisible = !overlayVisible },
                                )
                            }
                            is ViewerImageState.JpegView -> {
                                val request = remember(image.model, asset.id) {
                                    ImageRequest.Builder(context)
                                        .data(image.model)
                                        .listener(onError = { _, _ ->
                                            failedDecodeCache = viewerCacheKindAfterDecodeFailure(useJpegView = true)
                                            imageState = ViewerImageState.Error
                                        })
                                        .build()
                                }
                                ZoomableAsyncImage(
                                    model = request,
                                    contentDescription = asset.fileName,
                                    modifier = Modifier.fillMaxSize(),
                                    state = zoomableImageState,
                                    contentScale = ContentScale.Fit,
                                    onClick = { overlayVisible = !overlayVisible },
                                )
                            }
                            else -> Unit
                        }
                    }
                }
                if (isCurrentPage && pageImageState == ViewerImageState.Loading) {
                    LoadingIndicator()
                }
                if (isCurrentPage && pageImageState == ViewerImageState.Error) {
                    Button(
                        onClick = {
                            scope.launch {
                                invalidateViewerCacheSafely {
                                    when (failedDecodeCache) {
                                        ViewerCacheKind.ORIGINAL -> app.fileCache.invalidateOriginal(asset.id)
                                        ViewerCacheKind.JPEG_VIEW -> app.fileCache.invalidateJpegView(
                                            asset.id,
                                            jpegViewModel?.previewFileId,
                                        )
                                        null -> Unit
                                    }
                                }
                                failedDecodeCache = null
                                imageState = ViewerImageState.Preview
                                useJpegView = requiresJpegView(asset.fileName, jpegViewModel != null)
                                retryCount++
                            }
                        },
                        modifier = Modifier.align(Alignment.BottomCenter).padding(bottom = 28.dp),
                    ) {
                        Text(stringResource(R.string.viewer_retry_original))
                    }
                }
                if (asset.isVideo) {
                    Icon(
                        imageVector = Icons.Filled.PlayCircle,
                        contentDescription = stringResource(R.string.viewer_play_video),
                        tint = Color.White,
                        modifier = Modifier.size(72.dp).clickable { onPlayVideo(asset) },
                    )
                }
            }
        }
    }

    Box(Modifier.fillMaxSize()) {
        AnimatedVisibility(
            visible = overlayVisible,
            modifier = Modifier.align(Alignment.TopCenter).fillMaxWidth().zIndex(1f),
            enter = fadeIn(tween(160)) + slideInVertically(tween(160)) { -it / 3 },
            exit = fadeOut(tween(140)) + slideOutVertically(tween(140)) { -it / 3 },
        ) {
            Box(
                Modifier
                    .fillMaxWidth()
                    .height(112.dp)
                    .background(
                        Brush.verticalGradient(listOf(Color.Black.copy(alpha = 0.78f), Color.Transparent)),
                    ),
            ) {
                Row(
                    modifier = Modifier.align(Alignment.TopStart).fillMaxWidth().padding(start = 4.dp, top = 8.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    IconButton(onClick = onDismiss) {
                        Icon(
                            imageVector = Icons.AutoMirrored.Filled.ArrowBack,
                            contentDescription = stringResource(R.string.viewer_back),
                            tint = Color.White,
                        )
                    }
                    Text(text = dateTimeText, color = Color.White)
                }
            }
        }
        AnimatedVisibility(
            visible = overlayVisible,
            modifier = Modifier.align(Alignment.BottomCenter).padding(bottom = 28.dp).zIndex(1f),
            enter = fadeIn(tween(160)) + slideInVertically(tween(160)) { it / 2 },
            exit = fadeOut(tween(140)) + slideOutVertically(tween(140)) { it / 2 },
        ) {
            CloudMediaActions(
                asset = currentAsset,
                infoEnabled = infoEnabled,
                onInfo = { sheetVisible = true },
                onDelete = onDelete,
                scope = scope,
            )
        }
    }

    if (infoEnabled && sheetVisible && isOpen) {
        ModalBottomSheet(
            onDismissRequest = { sheetVisible = false },
            sheetState = sheetState,
        ) {
            CloudMediaInfoContent(
                asset = currentAsset,
                state = infoState,
                onSave = infoViewModel::save,
            )
        }
    }
}
