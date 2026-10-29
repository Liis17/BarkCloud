package com.barkfluff.BarkCloud.ui.components

import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.AnimatedVisibilityScope
import androidx.compose.animation.Crossfade
import androidx.compose.animation.core.MutableTransitionState
import androidx.compose.animation.core.spring
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.animation.core.tween
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.AnchoredDraggableState
import androidx.compose.foundation.gestures.DraggableAnchors
import androidx.compose.foundation.gestures.Orientation
import androidx.compose.foundation.gestures.anchoredDraggable
import androidx.compose.foundation.gestures.animateTo
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.PlayCircle
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LoadingIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.nestedscroll.NestedScrollConnection
import androidx.compose.ui.input.nestedscroll.NestedScrollSource
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.Velocity
import androidx.compose.ui.unit.dp
import androidx.compose.ui.zIndex
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import coil3.compose.AsyncImage
import coil3.request.ImageRequest
import me.saket.telephoto.zoomable.EnabledZoomGestures
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
import kotlin.math.roundToInt

private sealed interface ViewerImageState {
    data object Preview : ViewerImageState
    data object Loading : ViewerImageState
    data class Original(val file: File) : ViewerImageState
    data class JpegView(val model: CloudImageModel) : ViewerImageState
    data object Error : ViewerImageState
}

private enum class CloudMediaPanelAnchor {
    Dismissed,
    Collapsed,
    Expanded,
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
internal fun CloudMediaViewerLayer(
    infoEnabled: Boolean,
    isOpen: Boolean,
    isZoomed: Boolean,
    onDismiss: () -> Unit,
    onPanelExpanded: (Boolean) -> Unit,
    photoContent: @Composable (infoExpanded: Boolean, onPhotoTap: () -> Unit) -> Unit,
    overlayContent: @Composable (visible: Boolean, onInfo: () -> Unit) -> Unit,
    infoContent: @Composable () -> Unit,
) {
    BoxWithConstraints(Modifier.fillMaxSize()) {
        val density = LocalDensity.current
        val heightPx = with(density) { maxHeight.toPx() }
        val panelHeightPx = heightPx * 0.6f
        val panelHeight = with(density) { panelHeightPx.toDp() }
        val anchors = remember(heightPx, panelHeightPx, infoEnabled) {
            DraggableAnchors {
                CloudMediaPanelAnchor.Dismissed at heightPx
                CloudMediaPanelAnchor.Collapsed at 0f
                if (infoEnabled) CloudMediaPanelAnchor.Expanded at -panelHeightPx
            }
        }
        val panelState = remember { AnchoredDraggableState(CloudMediaPanelAnchor.Collapsed) }
        val scope = rememberCoroutineScope()
        val currentOnDismiss by rememberUpdatedState(onDismiss)
        var overlayVisible by remember { mutableStateOf(true) }

        SideEffect { panelState.updateAnchors(anchors) }

        val infoExpanded = infoEnabled &&
            (panelState.currentValue == CloudMediaPanelAnchor.Expanded ||
                panelState.targetValue == CloudMediaPanelAnchor.Expanded)
        val offsetPx = panelState.offset.takeIf(Float::isFinite) ?: 0f
        val backdropAlpha = 1f - (offsetPx / heightPx.coerceAtLeast(1f)).coerceIn(0f, 1f) * 0.8f

        LaunchedEffect(infoExpanded) { onPanelExpanded(infoExpanded) }
        LaunchedEffect(isZoomed) {
            if (isZoomed) overlayVisible = false
        }
        LaunchedEffect(panelState.currentValue) {
            if (panelState.currentValue == CloudMediaPanelAnchor.Dismissed) currentOnDismiss()
        }
        LaunchedEffect(isOpen) {
            if (!isOpen && panelState.currentValue != CloudMediaPanelAnchor.Dismissed) {
                panelState.animateTo(CloudMediaPanelAnchor.Collapsed, spring())
            }
        }

        BackHandler(enabled = isOpen) {
            if (infoExpanded) {
                scope.launch { panelState.animateTo(CloudMediaPanelAnchor.Collapsed, spring()) }
            } else {
                onDismiss()
            }
        }

        val panelScrollConnection = remember(panelState, panelHeightPx, infoEnabled) {
            object : NestedScrollConnection {
                private var panelWasDragged = false

                private fun consumePanelDelta(delta: Float): Float {
                    val start = panelState.offset
                    if (!start.isFinite()) return 0f
                    // Свойства могут свернуть панель только до Collapsed, но не закрыть viewer.
                    val target = (start + delta).coerceIn(-panelHeightPx, 0f)
                    val consumed = panelState.dispatchRawDelta(target - start)
                    if (consumed != 0f) panelWasDragged = true
                    return consumed
                }

                override fun onPreScroll(available: Offset, source: NestedScrollSource): Offset {
                    if (!infoEnabled || available.y >= 0f) return Offset.Zero
                    return Offset(0f, consumePanelDelta(available.y))
                }

                override fun onPostScroll(
                    consumed: Offset,
                    available: Offset,
                    source: NestedScrollSource,
                ): Offset {
                    if (!infoEnabled || available.y <= 0f) return Offset.Zero
                    return Offset(0f, consumePanelDelta(available.y))
                }

                override suspend fun onPreFling(available: Velocity): Velocity {
                    if (infoEnabled && panelWasDragged && available.y < 0f) {
                        panelState.animateTo(CloudMediaPanelAnchor.Expanded, spring())
                        panelWasDragged = false
                        return available
                    }
                    return Velocity.Zero
                }

                override suspend fun onPostFling(consumed: Velocity, available: Velocity): Velocity {
                    if (!infoEnabled || !panelWasDragged) return Velocity.Zero
                    val target = when {
                        available.y > 0f -> CloudMediaPanelAnchor.Collapsed
                        available.y < 0f -> CloudMediaPanelAnchor.Expanded
                        panelState.offset < -panelHeightPx / 2f -> CloudMediaPanelAnchor.Expanded
                        else -> CloudMediaPanelAnchor.Collapsed
                    }
                    panelState.animateTo(target, spring())
                    panelWasDragged = false
                    return available
                }
            }
        }

        Box(
            Modifier
                .fillMaxSize()
                .background(Color.Black.copy(alpha = backdropAlpha)),
        ) {
            Box(
                Modifier
                    .fillMaxSize()
                    .graphicsLayer { translationY = offsetPx }
                    .anchoredDraggable(
                        state = panelState,
                        orientation = Orientation.Vertical,
                        enabled = !isZoomed,
                    ),
            ) {
                photoContent(infoExpanded) {
                    if (infoExpanded) {
                        scope.launch { panelState.animateTo(CloudMediaPanelAnchor.Collapsed, spring()) }
                    } else {
                        overlayVisible = !overlayVisible
                    }
                }
                overlayContent(overlayVisible && !isZoomed && !infoExpanded) {
                    if (infoEnabled) {
                        val target = if (infoExpanded) {
                            CloudMediaPanelAnchor.Collapsed
                        } else {
                            CloudMediaPanelAnchor.Expanded
                        }
                        scope.launch { panelState.animateTo(target, spring()) }
                    }
                }
            }

            if (infoEnabled) {
                Surface(
                    modifier = Modifier
                        .align(Alignment.TopCenter)
                        .fillMaxWidth()
                        .height(panelHeight)
                        .offset { IntOffset(0, (heightPx + offsetPx).roundToInt()) }
                        .nestedScroll(panelScrollConnection),
                    shape = RoundedCornerShape(topStart = 24.dp, topEnd = 24.dp),
                    color = MaterialTheme.colorScheme.surface,
                ) {
                    infoContent()
                }
            }
        }
    }
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
    var listWasEmptyAtOpen by remember(selectedAsset?.id) {
        mutableStateOf(selectedAsset != null && assets.isEmpty())
    }

    LaunchedEffect(selectedAsset) {
        if (selectedAsset != null) retainedAsset = selectedAsset
        visibility.targetState = selectedAsset != null
    }
    LaunchedEffect(assets.isNotEmpty()) {
        if (assets.isNotEmpty()) listWasEmptyAtOpen = false
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
            if (assets.isEmpty() && !listWasEmptyAtOpen) {
                LaunchedEffect(asset.id) { onDismiss() }
            } else {
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
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
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
    val assetIds = remember(assets) { assets.map { it.id } }
    val scope = rememberCoroutineScope()
    var panelExpanded by remember { mutableStateOf(false) }
    val infoViewModel: CloudMediaInfoViewModel = viewModel(factory = CloudMediaInfoViewModel.factory())
    val currentAsset = assets.getOrNull(pagerState.currentPage) ?: return
    val dateTimeText = remember(currentAsset.createdAtMillis) {
        currentAsset.createdAtMillis.takeIf { it > 0L }?.let {
            DateFormat.getDateTimeInstance().format(Date(it))
        }.orEmpty()
    }
    val zoomStates = remember { mutableMapOf<String, ZoomableImageState>() }
    val activeZoomState = remember { mutableStateOf<ZoomableImageState?>(null) }
    val isZoomed by remember {
        derivedStateOf {
            (activeZoomState.value?.zoomableState?.contentTransformation?.scaleMetadata?.userZoom ?: 1f) > 1.01f
        }
    }
    var previousAssetIds by remember { mutableStateOf(assetIds) }
    var previousSettledAssetId by remember { mutableStateOf(assetIds.getOrNull(initialIndex)) }
    val infoState by infoViewModel.state.collectAsStateWithLifecycle()
    val viewerAssetId = LocalCloudMediaViewerAssetId.current
    val jpegViewModel = currentAsset.jpegViewModel()
    var useJpegView by remember(currentAsset.id) {
        mutableStateOf(requiresJpegView(currentAsset.fileName, jpegViewModel != null))
    }
    var retryCount by remember(currentAsset.id) { mutableIntStateOf(0) }
    var imageState by remember(currentAsset.id) { mutableStateOf<ViewerImageState>(ViewerImageState.Preview) }
    var failedDecodeCache by remember(currentAsset.id) { mutableStateOf<ViewerCacheKind?>(null) }

    LaunchedEffect(assetIds) {
        val targetIndex = viewerIndexAfterAssetListChange(
            previousIds = previousAssetIds,
            currentId = previousSettledAssetId,
            currentIds = assetIds,
        ) ?: run {
            onDismiss()
            return@LaunchedEffect
        }
        val targetAssetId = assetIds.getOrNull(targetIndex)
        val previousAssetId = previousSettledAssetId
        if (previousAssetId != null && previousAssetId != targetAssetId) {
            zoomStates.remove(previousAssetId)?.zoomableState?.resetZoom()
        }
        if (pagerState.currentPage != targetIndex) pagerState.scrollToPage(targetIndex)
        previousAssetIds = assetIds
        previousSettledAssetId = targetAssetId
    }

    LaunchedEffect(pagerState.settledPage, assetIds) {
        val settledAsset = assets.getOrNull(pagerState.settledPage) ?: return@LaunchedEffect
        val previousAssetId = previousSettledAssetId
        if (previousAssetId != null && previousAssetId != settledAsset.id) {
            zoomStates.remove(previousAssetId)?.zoomableState?.resetZoom()
        }
        previousSettledAssetId = settledAsset.id
    }

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

    LaunchedEffect(panelExpanded, currentAsset.id, isOpen) {
        if (panelExpanded && isOpen) infoViewModel.load(currentAsset.id)
    }

    CloudMediaViewerLayer(
        infoEnabled = infoEnabled,
        isOpen = isOpen,
        isZoomed = isZoomed,
        onDismiss = onDismiss,
        onPanelExpanded = { panelExpanded = it },
        photoContent = { infoExpanded, onPhotoTap ->
            val zoomGestures = when {
                infoExpanded -> EnabledZoomGestures.None
                isZoomed -> EnabledZoomGestures.ZoomAndPan
                else -> EnabledZoomGestures.ZoomOnly
            }
            Box(Modifier.fillMaxSize()) {
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
                            modifier = previewModifier.clickable(onClick = onPhotoTap),
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
                                            gestures = zoomGestures,
                                            onClick = { onPhotoTap() },
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
                                            gestures = zoomGestures,
                                            onClick = { onPhotoTap() },
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
        },
        overlayContent = { overlayVisible, onInfo ->
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
                                Brush.verticalGradient(
                                    listOf(Color.Black.copy(alpha = 0.78f), Color.Transparent),
                                ),
                            ),
                    ) {
                        Row(
                            modifier = Modifier
                                .align(Alignment.TopStart)
                                .fillMaxWidth()
                                .padding(start = 4.dp, top = 8.dp),
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
                        onInfo = onInfo,
                        onDelete = onDelete,
                        scope = scope,
                    )
                }
            }
        },
        infoContent = {
            CloudMediaInfoContent(
                asset = currentAsset,
                state = infoState,
                onSave = infoViewModel::save,
            )
        },
    )
}
