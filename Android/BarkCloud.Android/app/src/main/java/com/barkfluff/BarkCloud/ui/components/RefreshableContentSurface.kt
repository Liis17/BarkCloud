package com.barkfluff.BarkCloud.ui.components

import androidx.compose.animation.Crossfade
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.LoadingIndicatorDefaults
import androidx.compose.material3.LoadingIndicator
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Surface
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.material3.pulltorefresh.PullToRefreshDefaults
import androidx.compose.material3.pulltorefresh.rememberPullToRefreshState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.layout.layout
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.dp
import kotlin.math.roundToInt
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

internal fun contentSurfaceTopPx(
    headerHeightPx: Float,
    collapsedHeaderPx: Float,
): Float = headerHeightPx - collapsedHeaderPx

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
internal fun RefreshIndicator(
    pullProgress: () -> Float,
    isRefreshing: Boolean,
    gapHeightPx: () -> Float,
    modifier: Modifier = Modifier,
) {
    Box(
        modifier.fillMaxWidth().layout { measurable, constraints ->
            val height = gapHeightPx().roundToInt().coerceIn(0, constraints.maxHeight)
            val placeable = measurable.measure(constraints.copy(minHeight = height, maxHeight = height))
            layout(placeable.width, height) { placeable.placeRelative(0, 0) }
        },
        contentAlignment = Alignment.Center,
    ) {
        if (isRefreshing || pullProgress() > 0f) {
            val polygons = remember {
                val pool = LoadingIndicatorDefaults.IndeterminateIndicatorPolygons
                val start = pool.indices.random()
                pool.drop(start) + pool.take(start)
            }
            Crossfade(targetState = isRefreshing, label = "refreshIndicator") { refreshing ->
                if (refreshing) LoadingIndicator(polygons = polygons)
                else LoadingIndicator(progress = pullProgress, polygons = polygons)
            }
        }
    }
}

@Composable
internal fun RefreshContentSurface(
    modifier: Modifier = Modifier,
    topOffsetPx: () -> Float,
    pullOffsetPx: () -> Float = { 0f },
    content: @Composable BoxScope.() -> Unit,
) {
    Surface(
        modifier = modifier.fillMaxSize().clipToBounds().refreshSurfaceLayout(topOffsetPx, pullOffsetPx),
        shape = RoundedCornerShape(topStart = 28.dp, topEnd = 28.dp),
        color = MaterialTheme.colorScheme.surfaceVariant,
    ) {
        Box(Modifier.fillMaxSize(), content = content)
    }
}

private fun Modifier.refreshSurfaceLayout(
    topOffsetPx: () -> Float,
    pullOffsetPx: () -> Float,
): Modifier =
    layout { measurable, constraints ->
        val top = topOffsetPx().roundToInt().coerceIn(0, constraints.maxHeight)
        val contentHeight = constraints.maxHeight - top
        val placeable = measurable.measure(
            constraints.copy(minHeight = contentHeight, maxHeight = contentHeight),
        )
        layout(constraints.maxWidth, constraints.maxHeight) {
            // Pulling translates the surface and content without resizing the scroll viewport.
            placeable.placeRelative(0, top + pullOffsetPx().roundToInt())
        }
    }

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun RefreshableContentSurface(
    onRefresh: suspend () -> Unit,
    header: @Composable () -> Unit,
    modifier: Modifier = Modifier,
    content: @Composable BoxScope.() -> Unit,
) {
    val state = rememberPullToRefreshState()
    val scope = rememberCoroutineScope()
    val thresholdPx = with(LocalDensity.current) { PullToRefreshDefaults.PositionalThreshold.toPx() }
    var headerHeightPx by remember { mutableIntStateOf(0) }
    var refreshing by remember { mutableStateOf(false) }
    // Индикатор проявляется вместе с оттягиванием и держится полностью видимым во время обновления;
    // заголовок уходит ровно на обратную величину, поэтому переход идёт без скачков.
    val indicatorAlpha = { if (refreshing) 1f else state.distanceFraction.coerceIn(0f, 1f) }

    Box(modifier.fillMaxSize().background(MaterialTheme.colorScheme.background)) {
        Box(
            Modifier.fillMaxWidth()
                .onSizeChanged { headerHeightPx = it.height }
                .graphicsLayer { alpha = 1f - indicatorAlpha() },
        ) { header() }
        PullToRefreshBox(
            state = state,
            isRefreshing = refreshing,
            onRefresh = {
                if (!refreshing) {
                    refreshing = true
                    scope.launch {
                        try {
                            coroutineScope {
                                launch { delay(1_500) }
                                onRefresh()
                            }
                        } finally {
                            refreshing = false
                        }
                    }
                }
            },
            modifier = Modifier.fillMaxSize(),
            indicator = {
                RefreshIndicator(
                    pullProgress = { state.distanceFraction },
                    isRefreshing = refreshing,
                    gapHeightPx = { headerHeightPx + thresholdPx * state.distanceFraction },
                    modifier = Modifier.align(Alignment.TopCenter).graphicsLayer { alpha = indicatorAlpha() },
                )
            },
        ) {
            RefreshContentSurface(
                topOffsetPx = { headerHeightPx.toFloat() },
                pullOffsetPx = { thresholdPx * state.distanceFraction },
                content = content,
            )
        }
    }
}
