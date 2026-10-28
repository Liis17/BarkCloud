package com.barkfluff.BarkCloud.ui.components

import androidx.compose.animation.ExperimentalSharedTransitionApi
import androidx.compose.animation.AnimatedVisibilityScope
import androidx.compose.animation.SharedTransitionLayout
import androidx.compose.animation.SharedTransitionScope
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.Modifier

@OptIn(ExperimentalSharedTransitionApi::class)
private val LocalSharedTransitionScope = staticCompositionLocalOf<SharedTransitionScope?> { null }

internal val LocalCloudMediaViewerAssetId = staticCompositionLocalOf<String?> { null }

@OptIn(ExperimentalSharedTransitionApi::class)
@Composable
internal fun CloudMediaTransitionLayout(
    viewerAssetId: String?,
    content: @Composable () -> Unit,
) {
    SharedTransitionLayout(Modifier.fillMaxSize()) {
        CompositionLocalProvider(
            LocalSharedTransitionScope provides this,
            LocalCloudMediaViewerAssetId provides viewerAssetId,
            content = content,
        )
    }
}

@OptIn(ExperimentalSharedTransitionApi::class)
@Composable
internal fun Modifier.cloudMediaSharedElement(
    key: String,
    visible: Boolean,
    animatedVisibilityScope: AnimatedVisibilityScope? = null,
): Modifier {
    val scope = LocalSharedTransitionScope.current ?: return this
    return with(scope) {
        val state = rememberSharedContentState(key = "cloud-media:$key")
        if (animatedVisibilityScope != null) {
            // Keep the viewer alive until its shared-element exit has finished.
            this@cloudMediaSharedElement.sharedElement(state, animatedVisibilityScope)
        } else {
            this@cloudMediaSharedElement.sharedElementWithCallerManagedVisibility(state, visible)
        }
    }
}
