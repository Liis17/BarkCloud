package com.barkfluff.BarkCloud.ui.gallery

import android.content.Context
import android.content.Intent
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.MutableTransitionState
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.PlayCircle
import androidx.compose.material.icons.outlined.Delete
import androidx.compose.material.icons.outlined.Share
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.compose.ui.zIndex
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.gallery.DeviceMedia
import com.barkfluff.BarkCloud.ui.components.CloudMediaViewerLayer
import me.saket.telephoto.zoomable.EnabledZoomGestures
import me.saket.telephoto.zoomable.ZoomableImageState
import me.saket.telephoto.zoomable.coil3.ZoomableAsyncImage
import me.saket.telephoto.zoomable.rememberZoomableImageState

/**
 * Полноэкранный просмотр локальных фото и видео. Листание идёт по переданному списку
 * (текущая секция галереи или папка устройства), жесты и закрытие — общая оболочка облачного просмотрщика.
 * Тап переключает оверлей и не закрывает просмотр. Видео пока открывается во внешнем проигрывателе.
 * [onDelete] задаётся только там, где уже поддерживается системное удаление MediaStore.
 */
@Composable
fun DeviceMediaViewer(
    items: List<DeviceMedia>,
    selected: DeviceMedia?,
    onDismiss: () -> Unit,
    onPlayVideo: (DeviceMedia) -> Unit,
    onDelete: ((DeviceMedia) -> Unit)? = null,
) {
    val visibility = remember { MutableTransitionState(false) }
    var retained by remember { mutableStateOf<DeviceMedia?>(null) }

    LaunchedEffect(selected) {
        if (selected != null) retained = selected
        visibility.targetState = selected != null
    }
    LaunchedEffect(visibility.isIdle, visibility.currentState) {
        if (visibility.isIdle && !visibility.currentState) retained = null
    }

    // Во время fade-out список уже мог опустеть (вызывающий снял selected); показываем последний открытый список.
    val lastOpenItems = remember { mutableStateOf(items) }
    SideEffect { if (selected != null) lastOpenItems.value = items }
    val pagerItems = if (selected != null) items else lastOpenItems.value

    val shown = retained ?: selected ?: return
    Dialog(
        onDismissRequest = onDismiss,
        properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false),
    ) {
        AnimatedVisibility(
            visibleState = visibility,
            modifier = Modifier.fillMaxSize().zIndex(1f),
            enter = fadeIn(tween(180)),
            exit = fadeOut(tween(180)),
        ) {
            key(shown.mediaKey) {
                DeviceMediaPager(
                    items = pagerItems,
                    initialIndex = pagerItems.indexOfFirst { it.mediaKey == shown.mediaKey }.coerceAtLeast(0),
                    isOpen = selected?.mediaKey == shown.mediaKey,
                    onDismiss = onDismiss,
                    onPlayVideo = onPlayVideo,
                    onDelete = onDelete,
                )
            }
        }
    }
}

@Composable
private fun DeviceMediaPager(
    items: List<DeviceMedia>,
    initialIndex: Int,
    isOpen: Boolean,
    onDismiss: () -> Unit,
    onPlayVideo: (DeviceMedia) -> Unit,
    onDelete: ((DeviceMedia) -> Unit)?,
) {
    val context = LocalContext.current
    val pagerState = rememberPagerState(initialPage = initialIndex) { items.size }
    val zoomStates = remember { mutableMapOf<String, ZoomableImageState>() }
    val activeZoomState = remember { mutableStateOf<ZoomableImageState?>(null) }
    val isZoomed by remember {
        derivedStateOf {
            (activeZoomState.value?.zoomableState?.contentTransformation?.scaleMetadata?.userZoom ?: 1f) > 1.01f
        }
    }
    // Удаление последнего элемента закрывает просмотр; остальные страницы сдвигает сам пейджер по стабильным ключам.
    LaunchedEffect(items.isEmpty()) {
        if (items.isEmpty()) onDismiss()
    }
    val currentMedia = items.getOrNull(pagerState.currentPage)

    CloudMediaViewerLayer(
        infoEnabled = false,
        isOpen = isOpen,
        isZoomed = isZoomed,
        onDismiss = onDismiss,
        onPanelExpanded = {},
        photoContent = { _, onPhotoTap ->
            HorizontalPager(
                state = pagerState,
                key = { page -> items[page].mediaKey },
                modifier = Modifier.fillMaxSize(),
            ) { page ->
                val media = items.getOrNull(page) ?: return@HorizontalPager
                val isCurrentPage = page == pagerState.currentPage
                val zoomableImageState = rememberZoomableImageState()
                SideEffect {
                    zoomStates[media.mediaKey] = zoomableImageState
                    if (isCurrentPage) activeZoomState.value = zoomableImageState
                }
                Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    ZoomableAsyncImage(
                        model = media.uri,
                        contentDescription = media.name,
                        modifier = Modifier.fillMaxSize(),
                        state = zoomableImageState,
                        contentScale = ContentScale.Fit,
                        gestures = if (isZoomed && isCurrentPage) EnabledZoomGestures.ZoomAndPan else EnabledZoomGestures.ZoomOnly,
                        onClick = { onPhotoTap() },
                    )
                    if (media.isVideo) {
                        Icon(
                            imageVector = Icons.Filled.PlayCircle,
                            contentDescription = stringResource(R.string.viewer_play_video),
                            tint = Color.White,
                            modifier = Modifier.size(72.dp).clickable { onPlayVideo(media) },
                        )
                    }
                }
            }
        },
        overlayContent = { overlayVisible, _ ->
            if (overlayVisible && currentMedia != null) {
                Box(Modifier.fillMaxSize()) {
                    Row(
                        modifier = Modifier
                            .align(Alignment.TopStart)
                            .fillMaxWidth()
                            .background(Color.Black.copy(alpha = 0.5f))
                            .statusBarsPadding()
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
                        Text(text = currentMedia.name, color = Color.White, modifier = Modifier.weight(1f))
                    }
                    Row(
                        modifier = Modifier
                            .align(Alignment.BottomCenter)
                            .navigationBarsPadding()
                            .padding(bottom = 16.dp),
                    ) {
                        IconButton(onClick = { shareLocalMedia(context, currentMedia) }) {
                            Icon(
                                imageVector = Icons.Outlined.Share,
                                contentDescription = stringResource(R.string.viewer_action_share),
                                tint = Color.White,
                            )
                        }
                        if (onDelete != null) {
                            IconButton(onClick = { onDelete(currentMedia) }) {
                                Icon(
                                    imageVector = Icons.Outlined.Delete,
                                    contentDescription = stringResource(R.string.viewer_action_delete),
                                    tint = Color.White,
                                )
                            }
                        }
                    }
                }
            }
        },
        infoContent = {},
    )
}

private fun shareLocalMedia(context: Context, media: DeviceMedia) {
    val intent = Intent(Intent.ACTION_SEND)
        .setType(media.mimeType ?: if (media.isVideo) "video/*" else "image/*")
        .putExtra(Intent.EXTRA_STREAM, media.uri)
        .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
    runCatching {
        context.startActivity(Intent.createChooser(intent, context.getString(R.string.viewer_action_share_title)))
    }
}
