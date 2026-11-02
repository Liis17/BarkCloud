package com.barkfluff.BarkCloud.ui.gallery

import android.animation.ValueAnimator
import android.app.Activity
import android.provider.MediaStore
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.IntentSenderRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.ExitTransition
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.CubicBezierEasing
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Close
import androidx.compose.material3.Button
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.gallery.DeviceMedia
import com.barkfluff.BarkCloud.data.gallery.MediaCloudStatus
import com.barkfluff.BarkCloud.files.ui.formatSize
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
import kotlin.math.sqrt

/** Idle → Checking (проверка хешей и системный диалог) → Running (волна) → Done. */
internal enum class CleanupPhase { Idle, Checking, Running, Done }

private val HeaderHeight = 72.dp
private val PanelHeight = 176.dp
private val cssEase = CubicBezierEasing(0.25f, 0.1f, 0.25f, 1f)
private val barEasing = CubicBezierEasing(0.5f, 0f, 1f, 1f)

@Composable
internal fun DeviceCleanupScreen(onNavigateUp: () -> Unit, viewModel: GalleryViewModel) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val deletion by viewModel.deletionState.collectAsStateWithLifecycle()
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val snackbar = remember { SnackbarHostState() }
    var phase by rememberSaveable { mutableStateOf(CleanupPhase.Idle) }
    var freedBytes by rememberSaveable { mutableLongStateOf(0L) }
    var departing by remember { mutableStateOf<List<DeviceMedia>>(emptyList()) }
    var pending by rememberSaveable(stateSaver = pendingPhotoDeleteSaver) { mutableStateOf<PendingPhotoDelete?>(null) }
    val launcher = rememberLauncherForActivityResult(ActivityResultContracts.StartIntentSenderForResult()) { result ->
        val approved = result.resultCode == Activity.RESULT_OK
        if (approved) {
            val keys = pending?.deviceMediaKeys.orEmpty().toSet()
            departing = state.items.filter { it.mediaKey in keys }
            phase = CleanupPhase.Running
        } else {
            phase = CleanupPhase.Idle
        }
        pending?.let { viewModel.completeSystemDelete(it, approved) }
        pending = null
    }
    LaunchedEffect(phase) {
        // После пересоздания процесса снимок плиток потерян: удаление уже подтверждено, показываем итог.
        if (phase == CleanupPhase.Running && departing.isEmpty()) phase = CleanupPhase.Done
    }
    LaunchedEffect(deletion.results.firstOrNull()?.id) {
        val result = deletion.results.firstOrNull() ?: return@LaunchedEffect
        viewModel.acknowledgeDeleteResult(result.id)
        // В фазах волны и итога объём уже показан на панели.
        if (phase == CleanupPhase.Idle || phase == CleanupPhase.Checking) {
            snackbar.showSnackbar(context.getString(R.string.gallery_device_copies_deleted, result.deletedCount))
        }
    }
    DeviceCleanupContent(
        state = state,
        phase = phase,
        busy = deletion.isBusy,
        freedBytes = freedBytes,
        departing = departing,
        onClose = onNavigateUp,
        onAnimationFinished = {
            phase = CleanupPhase.Done
            departing = emptyList()
        },
        snackbarHost = { SnackbarHost(snackbar) },
        onClean = {
            phase = CleanupPhase.Checking
            scope.launch {
                try {
                    val candidates = viewModel.prepareDeviceCopyDeletion()
                    if (candidates.isEmpty()) {
                        phase = CleanupPhase.Idle
                        snackbar.showSnackbar(context.getString(R.string.gallery_cleanup_none_confirmed))
                    } else {
                        val request = MediaStore.createDeleteRequest(context.contentResolver, candidates.map { it.uri })
                        val deletionRequest = PendingPhotoDelete(
                            candidates.map { it.mediaKey }, emptyList(), candidates.size, accountReclaim = true,
                        )
                        if (viewModel.beginSystemDelete(deletionRequest)) {
                            freedBytes = candidates.sumOf { it.sizeBytes.coerceAtLeast(0) }
                            pending = deletionRequest
                            launcher.launch(IntentSenderRequest.Builder(request.intentSender).build())
                        } else {
                            phase = CleanupPhase.Idle
                        }
                    }
                } catch (error: CancellationException) {
                    throw error
                } catch (error: Exception) {
                    pending = null
                    viewModel.abortSystemDelete()
                    phase = CleanupPhase.Idle
                    snackbar.showSnackbar(error.message ?: context.getString(R.string.common_error_generic))
                }
            }
        },
    )
}

/** Разметка макета «C · Волна»: шапка, сетка под ней, панель снизу и экран «Готово». */
@Composable
internal fun DeviceCleanupContent(
    state: GalleryUiState,
    phase: CleanupPhase,
    onClean: () -> Unit,
    onClose: () -> Unit,
    modifier: Modifier = Modifier,
    busy: Boolean = false,
    freedBytes: Long = 0L,
    departing: List<DeviceMedia> = emptyList(),
    onAnimationFinished: () -> Unit = {},
    snackbarHost: @Composable () -> Unit = {},
) {
    val elapsed = remember { Animatable(0f) }
    val reclaimable = remember(state.items, state.cloudStates) {
        state.items.filter { state.cloudStates[it.mediaKey] == MediaCloudStatus.IN_CLOUD }
    }
    val colors = MaterialTheme.colorScheme
    Box(modifier.fillMaxSize().background(colors.surface)) {
        val running = phase == CleanupPhase.Running
        if (phase != CleanupPhase.Done) {
            // Один вызов для idle и волны: плитки сохраняют записанные слои для снимков частиц.
            CleanupWaveGrid(
                items = if (running) departing else reclaimable,
                running = running,
                elapsed = elapsed,
                onFinished = onAnimationFinished,
                modifier = Modifier.fillMaxSize().padding(top = HeaderHeight),
            )
        }
        if (!running && phase != CleanupPhase.Done && reclaimable.isEmpty() && !state.isLoading) {
            Text(
                stringResource(R.string.gallery_cleanup_empty),
                style = MaterialTheme.typography.bodyMedium,
                color = colors.onSurfaceVariant,
                textAlign = TextAlign.Center,
                modifier = Modifier
                    .align(Alignment.Center)
                    .padding(top = HeaderHeight, bottom = PanelHeight, start = 32.dp, end = 32.dp),
            )
        }
        if (phase == CleanupPhase.Done) {
            CleanupWin(Modifier.fillMaxSize().padding(top = HeaderHeight, bottom = PanelHeight))
        }
        CleanupHeader(onClose)
        Box(Modifier.align(Alignment.BottomCenter).padding(bottom = PanelHeight)) { snackbarHost() }
        CleanupPanel(
            state = state,
            phase = phase,
            busy = busy,
            freedBytes = freedBytes,
            barProgress = { barEasing.transform(elapsed.value.coerceIn(0f, 1f)) },
            onClean = onClean,
            onClose = onClose,
            modifier = Modifier.align(Alignment.BottomCenter),
        )
    }
}

@Composable
private fun CleanupHeader(onClose: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .height(HeaderHeight)
            .background(MaterialTheme.colorScheme.surface)
            .padding(horizontal = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        IconButton(onClick = onClose, modifier = Modifier.size(48.dp)) {
            Icon(Icons.Outlined.Close, stringResource(R.string.gallery_cleanup_close))
        }
        Column(Modifier.weight(1f).padding(start = 4.dp)) {
            Text(
                stringResource(R.string.gallery_cleanup_title),
                style = MaterialTheme.typography.titleLarge.copy(fontWeight = FontWeight.Medium),
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                stringResource(R.string.gallery_cleanup_subtitle),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
    }
}

private enum class PanelKind { Idle, Busy, Done }

private val CleanupPhase.panel: PanelKind
    get() = when (this) {
        CleanupPhase.Idle -> PanelKind.Idle
        CleanupPhase.Checking, CleanupPhase.Running -> PanelKind.Busy
        CleanupPhase.Done -> PanelKind.Done
    }

@Composable
private fun CleanupPanel(
    state: GalleryUiState,
    phase: CleanupPhase,
    busy: Boolean,
    freedBytes: Long,
    barProgress: () -> Float,
    onClean: () -> Unit,
    onClose: () -> Unit,
    modifier: Modifier,
) {
    val context = LocalContext.current
    val colors = MaterialTheme.colorScheme
    Surface(
        modifier = modifier.fillMaxWidth().heightIn(min = PanelHeight),
        shape = RoundedCornerShape(topStart = 28.dp, topEnd = 28.dp),
        color = colors.surfaceContainer,
        shadowElevation = 1.dp,
    ) {
        AnimatedContent(
            targetState = phase,
            contentKey = { it.panel },
            transitionSpec = {
                fadeIn(tween(if (targetState == CleanupPhase.Done) 400 else 200, easing = cssEase)) togetherWith
                    ExitTransition.None
            },
            label = "cleanupPanel",
        ) { current ->
            Column(
                Modifier
                    .fillMaxWidth()
                    .heightIn(min = PanelHeight - 44.dp)
                    .padding(start = 16.dp, end = 16.dp, top = 20.dp, bottom = 24.dp),
                verticalArrangement = Arrangement.SpaceBetween,
            ) {
                val (title, hint) = when (current) {
                    CleanupPhase.Idle -> stringResource(
                        R.string.gallery_cleanup_idle_title, formatSize(context, state.reclaimableBytes),
                    ) to stringResource(R.string.gallery_cleanup_idle_hint)
                    CleanupPhase.Checking -> stringResource(
                        R.string.gallery_cleanup_busy_title, formatSize(context, state.reclaimableBytes),
                    ) to stringResource(R.string.gallery_cleanup_checking)
                    CleanupPhase.Running -> stringResource(
                        R.string.gallery_cleanup_busy_title, formatSize(context, freedBytes),
                    ) to stringResource(R.string.gallery_cleanup_busy_hint)
                    CleanupPhase.Done -> stringResource(
                        R.string.gallery_cleanup_done_title, formatSize(context, freedBytes),
                    ) to stringResource(R.string.gallery_cleanup_done_hint)
                }
                Column(verticalArrangement = Arrangement.spacedBy(16.dp)) {
                    Column {
                        Text(title, style = MaterialTheme.typography.titleMedium)
                        Text(hint, style = MaterialTheme.typography.bodySmall, color = colors.onSurfaceVariant)
                    }
                    val barModifier = Modifier.fillMaxWidth().height(4.dp)
                    when (current) {
                        CleanupPhase.Checking -> LinearProgressIndicator(
                            modifier = barModifier,
                            color = colors.primary,
                            trackColor = colors.surfaceContainerHighest,
                            strokeCap = StrokeCap.Round,
                            gapSize = 0.dp,
                        )
                        CleanupPhase.Running -> LinearProgressIndicator(
                            progress = barProgress,
                            modifier = barModifier,
                            color = colors.primary,
                            trackColor = colors.surfaceContainerHighest,
                            strokeCap = StrokeCap.Round,
                            gapSize = 0.dp,
                            drawStopIndicator = {},
                        )
                        else -> Unit
                    }
                }
                val buttonModifier = Modifier.padding(top = 12.dp).fillMaxWidth().height(48.dp)
                when (current) {
                    CleanupPhase.Idle -> Button(
                        onClick = onClean,
                        enabled = !busy && !state.isLoading && !state.isQueueing && state.reclaimableBytes > 0,
                        modifier = buttonModifier,
                    ) { Text(stringResource(R.string.gallery_free_device_space)) }
                    CleanupPhase.Checking, CleanupPhase.Running -> Button(
                        onClick = {},
                        enabled = false,
                        modifier = buttonModifier,
                    ) { Text(stringResource(R.string.gallery_cleanup_busy_action)) }
                    CleanupPhase.Done -> Button(onClick = onClose, modifier = buttonModifier) {
                        Text(stringResource(R.string.gallery_cleanup_close))
                    }
                }
            }
        }
    }
}

/** Итог: диск с «выпрыгиванием» и прорисовкой галочки, как `.win` в макете. */
@Composable
private fun CleanupWin(modifier: Modifier) {
    val appear = remember { Animatable(0f) }
    val pop = remember { Animatable(0.6f) }
    val draw = remember { Animatable(0f) }
    LaunchedEffect(Unit) {
        if (!ValueAnimator.areAnimatorsEnabled()) {
            appear.snapTo(1f)
            pop.snapTo(1f)
            draw.snapTo(1f)
            return@LaunchedEffect
        }
        launch { appear.animateTo(1f, tween(300, easing = cssEase)) }
        launch { pop.animateTo(1f, tween(400, easing = cssEase)) }
        draw.animateTo(1f, tween(500, delayMillis = 300, easing = cssEase))
    }
    val colors = MaterialTheme.colorScheme
    Column(
        modifier
            .graphicsLayer { alpha = appear.value }
            .semantics { liveRegion = LiveRegionMode.Polite },
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(16.dp, Alignment.CenterVertically),
    ) {
        Box(
            Modifier
                .size(112.dp)
                .graphicsLayer {
                    scaleX = pop.value
                    scaleY = pop.value
                }
                .background(colors.primaryContainer, CircleShape),
            contentAlignment = Alignment.Center,
        ) {
            val stroke = colors.primary
            Canvas(Modifier.size(56.dp)) { drawCheck(stroke, draw.value) }
        }
        Text(
            stringResource(R.string.gallery_cleanup_win_title),
            style = MaterialTheme.typography.headlineSmall.copy(fontWeight = FontWeight.Medium),
            textAlign = TextAlign.Center,
        )
        Text(
            stringResource(R.string.gallery_cleanup_win_body),
            style = MaterialTheme.typography.bodyMedium,
            color = colors.onSurfaceVariant,
            textAlign = TextAlign.Center,
            modifier = Modifier.widthIn(max = 280.dp),
        )
    }
}

/**
 * Галочка `M5 12.5l4.5 4.5L19 7.5` (viewBox 24) со штрихом 40 и смещением 40→0, как в макете:
 * видимая длина появляется во второй половине анимации.
 */
private fun DrawScope.drawCheck(color: Color, progress: Float) {
    val unit = size.width / 24f
    val first = 4.5f * sqrt(2f)
    val second = 9.5f * sqrt(2f)
    val visible = (first + second - 40f * (1f - progress)).coerceIn(0f, first + second)
    if (visible <= 0f) return
    val start = Offset(5f, 12.5f)
    val corner = Offset(9.5f, 17f)
    val end = Offset(19f, 7.5f)
    val path = Path().apply {
        moveTo(start.x * unit, start.y * unit)
        if (visible <= first) {
            val point = start + (corner - start) * (visible / first)
            lineTo(point.x * unit, point.y * unit)
        } else {
            lineTo(corner.x * unit, corner.y * unit)
            val point = corner + (end - corner) * ((visible - first) / second)
            lineTo(point.x * unit, point.y * unit)
        }
    }
    drawPath(path, color, style = Stroke(width = 2.5f * unit, cap = StrokeCap.Round, join = StrokeJoin.Round))
}
