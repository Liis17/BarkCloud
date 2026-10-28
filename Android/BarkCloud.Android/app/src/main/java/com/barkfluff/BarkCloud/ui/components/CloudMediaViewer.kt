package com.barkfluff.BarkCloud.ui.components

import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.Crossfade
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.core.MutableTransitionState
import androidx.compose.animation.core.animate
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectVerticalDragGestures
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.PlayCircle
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.Icon
import androidx.compose.material3.LoadingIndicator
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
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
    val currentAsset = assets[pagerState.currentPage]
    val infoState by infoViewModel.state.collectAsStateWithLifecycle()
    val viewerAssetId = LocalCloudMediaViewerAssetId.current
    val jpegViewModel = currentAsset.jpegViewModel()
    var useJpegView by remember(currentAsset.id) {
        mutableStateOf(requiresJpegView(currentAsset.fileName, jpegViewModel != null))
    }
    var retryCount by remember(currentAsset.id) { mutableIntStateOf(0) }
    var imageState by remember(currentAsset.id) { mutableStateOf<ViewerImageState>(ViewerImageState.Preview) }
    var failedDecodeCache by remember(currentAsset.id) { mutableStateOf<ViewerCacheKind?>(null) }

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
            .pointerInput(dismissPx, sheetOpenPx) {
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
        HorizontalPager(state = pagerState, modifier = Modifier.fillMaxSize()) { page ->
            val asset = assets[page]
            val isCurrentPage = page == pagerState.currentPage
            val pageImageState = if (isCurrentPage) imageState else ViewerImageState.Preview
            val previewModifier = Modifier.fillMaxSize().cloudMediaSharedElement(
                key = asset.id,
                visible = viewerAssetId == asset.id && isCurrentPage,
            )

            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                AsyncImage(
                    model = asset.previewModel(1024),
                    contentDescription = asset.fileName,
                    modifier = previewModifier,
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
                            is ViewerImageState.Original -> AsyncImage(
                                model = image.file,
                                contentDescription = asset.fileName,
                                modifier = Modifier.fillMaxSize(),
                                contentScale = ContentScale.Fit,
                                onError = {
                                    if (!useJpegView && jpegViewModel != null) {
                                        failedDecodeCache = null
                                        scope.launch { app.fileCache.invalidateOriginal(asset.id) }
                                        imageState = ViewerImageState.Loading
                                        useJpegView = true
                                    } else {
                                        failedDecodeCache = viewerCacheKindAfterDecodeFailure(useJpegView)
                                        imageState = ViewerImageState.Error
                                    }
                                },
                            )
                            is ViewerImageState.JpegView -> AsyncImage(
                                model = image.model,
                                contentDescription = asset.fileName,
                                modifier = Modifier.fillMaxSize(),
                                contentScale = ContentScale.Fit,
                                onError = {
                                    failedDecodeCache = viewerCacheKindAfterDecodeFailure(useJpegView = true)
                                    imageState = ViewerImageState.Error
                                },
                            )
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
                                when (failedDecodeCache) {
                                    ViewerCacheKind.ORIGINAL -> app.fileCache.invalidateOriginal(asset.id)
                                    ViewerCacheKind.JPEG_VIEW -> app.fileCache.invalidateJpegView(
                                        asset.id,
                                        jpegViewModel?.previewFileId,
                                    )
                                    null -> Unit
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
