package com.barkfluff.BarkCloud.ui.settings

import android.graphics.Bitmap
import android.net.Uri
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.gestures.detectTransformGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Close
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.FilterQuality
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.PathFillType
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.barkfluff.BarkCloud.R
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
import java.io.File
import kotlin.math.roundToInt

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun AvatarCropDialog(uri: Uri, onDismiss: () -> Unit, onConfirm: (File) -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var bitmap by remember(uri) { mutableStateOf<Bitmap?>(null) }
    var crop by remember(uri) { mutableStateOf(AvatarCrop()) }
    var error by remember(uri) { mutableStateOf<String?>(null) }
    var saving by remember { mutableStateOf(false) }
    LaunchedEffect(uri) {
        try {
            bitmap = AvatarImageProcessor.decode(context, uri)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            error = e.localizedMessage ?: context.getString(R.string.avatar_crop_error)
        }
    }
    Dialog(
        onDismissRequest = { if (!saving) onDismiss() },
        properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false),
    ) {
        Scaffold(
            topBar = {
                TopAppBar(
                    title = { Text(stringResource(R.string.avatar_crop_title)) },
                    navigationIcon = {
                        IconButton(onClick = onDismiss, enabled = !saving) {
                            Icon(Icons.Outlined.Close, contentDescription = stringResource(R.string.common_cancel))
                        }
                    },
                    actions = {
                        TextButton(enabled = bitmap != null && !saving, onClick = {
                            val source = bitmap ?: return@TextButton
                            saving = true
                            error = null
                            scope.launch {
                                try {
                                    val file = AvatarImageProcessor.writeJpeg(context, source, crop)
                                    try { onConfirm(file) } catch (e: Exception) { file.delete(); throw e }
                                } catch (e: CancellationException) {
                                    throw e
                                } catch (e: Exception) {
                                    error = e.localizedMessage ?: context.getString(R.string.avatar_crop_error)
                                } finally {
                                    saving = false
                                }
                            }
                        }) { Text(stringResource(R.string.avatar_crop_apply)) }
                    },
                )
            },
        ) { padding ->
            Column(
                Modifier.fillMaxSize().padding(padding).padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(16.dp),
            ) {
                BoxWithConstraints(Modifier.weight(1f).fillMaxWidth(), contentAlignment = Alignment.Center) {
                    val source = bitmap
                    if (source == null || saving) {
                        if (error == null) CircularProgressIndicator()
                    } else {
                        val image = remember(source) { source.asImageBitmap() }
                        val description = stringResource(R.string.avatar_crop_area)
                        Canvas(
                            Modifier.size(minOf(maxWidth, maxHeight))
                                .semantics { contentDescription = description }
                                .pointerInput(source) {
                                    detectTransformGestures { centroid, pan, zoom, _ ->
                                        val nextZoom = (crop.zoom * zoom).coerceIn(1f, 4f)
                                        val ratio = nextZoom / crop.zoom
                                        val focus = centroid / size.width.toFloat() - Offset(0.5f, 0.5f)
                                        crop = AvatarCrop(
                                            nextZoom,
                                            (crop.panX - focus.x) * ratio + focus.x + pan.x / size.width,
                                            (crop.panY - focus.y) * ratio + focus.y + pan.y / size.height,
                                        ).constrained(source.width, source.height)
                                    }
                                },
                        ) {
                            drawRect(Color.White)
                            val bounds = crop.pixels(source.width, source.height)
                            drawImage(
                                image,
                                srcOffset = IntOffset(bounds.left, bounds.top),
                                srcSize = IntSize(bounds.side, bounds.side),
                                dstSize = IntSize(size.width.roundToInt(), size.height.roundToInt()),
                                filterQuality = FilterQuality.Medium,
                            )
                            val mask = Path().apply {
                                fillType = PathFillType.EvenOdd
                                addRect(Rect(Offset.Zero, size))
                                addOval(Rect(Offset.Zero, size))
                            }
                            drawPath(mask, Color.Black.copy(alpha = 0.45f))
                            drawCircle(Color.White, radius = size.minDimension / 2 - 1.dp.toPx(), style = Stroke(2.dp.toPx()))
                        }
                    }
                }
                error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
                if (bitmap != null) {
                    Text(stringResource(R.string.avatar_crop_hint))
                    Text(stringResource(R.string.avatar_crop_zoom))
                    Slider(
                        value = crop.zoom,
                        onValueChange = { zoom ->
                            val ratio = zoom / crop.zoom
                            crop = AvatarCrop(zoom, crop.panX * ratio, crop.panY * ratio)
                                .constrained(bitmap!!.width, bitmap!!.height)
                        },
                        valueRange = 1f..4f,
                        enabled = !saving,
                    )
                }
            }
        }
    }
}
