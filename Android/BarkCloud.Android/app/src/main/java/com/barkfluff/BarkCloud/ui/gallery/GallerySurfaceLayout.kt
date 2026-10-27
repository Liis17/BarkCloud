package com.barkfluff.BarkCloud.ui.gallery

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.layout.layout
import androidx.compose.ui.unit.dp
import kotlin.math.roundToInt

internal fun gallerySurfaceTopPx(
    headerHeightPx: Float,
    collapsedHeaderPx: Float,
    pullOffsetPx: Float,
): Float = headerHeightPx - collapsedHeaderPx + pullOffsetPx

@Composable
internal fun GalleryContentSurface(
    modifier: Modifier = Modifier,
    topOffsetPx: () -> Float,
    content: @Composable BoxScope.() -> Unit,
) {
    Surface(
        modifier = modifier.fillMaxSize().gallerySurfaceLayout(topOffsetPx),
        shape = RoundedCornerShape(topStart = 28.dp, topEnd = 28.dp),
        color = MaterialTheme.colorScheme.surfaceVariant,
    ) {
        Box(Modifier.fillMaxSize(), content = content)
    }
}

internal fun Modifier.gallerySurfaceLayout(topOffsetPx: () -> Float): Modifier =
    layout { measurable, constraints ->
        val top = topOffsetPx().roundToInt().coerceIn(0, constraints.maxHeight)
        val contentHeight = constraints.maxHeight - top
        val placeable = measurable.measure(
            constraints.copy(minHeight = contentHeight, maxHeight = contentHeight),
        )
        layout(constraints.maxWidth, constraints.maxHeight) {
            placeable.placeRelative(0, top)
        }
    }
