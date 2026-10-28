package com.barkfluff.BarkCloud.ui.components

import androidx.compose.animation.ExperimentalSharedTransitionApi
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
internal fun Modifier.cloudMediaSharedElement(key: String, visible: Boolean): Modifier {
    val scope = LocalSharedTransitionScope.current ?: return this
    return with(scope) {
        this@cloudMediaSharedElement.sharedElementWithCallerManagedVisibility(
            rememberSharedContentState(key = "cloud-media:$key"),
            visible = visible,
        )
    }
}
