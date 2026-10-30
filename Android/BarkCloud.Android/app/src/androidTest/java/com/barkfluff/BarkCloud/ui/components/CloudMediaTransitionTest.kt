package com.barkfluff.BarkCloud.ui.components

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.test.swipeLeft
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.unit.dp
import org.junit.Rule
import org.junit.Test

class CloudMediaTransitionTest {
    @get:Rule val compose = createComposeRule()

    @Test
    fun repeatedlyClosingViewerAfterPagingAndDuringEntryDoesNotCrash() {
        val open = mutableStateOf(false)
        compose.setContent {
            CloudMediaTransitionLayout(if (open.value) "photo" else null) {
                repeat(3) { page ->
                    Box(Modifier.size(100.dp).cloudMediaSharedElement("photo-$page", !open.value || page != 0))
                }
                AnimatedVisibility(
                    visible = open.value,
                    enter = fadeIn(tween(180)),
                    exit = fadeOut(tween(180)),
                ) {
                    val visibilityScope = this
                    val pager = rememberPagerState { 3 }
                    HorizontalPager(pager, Modifier.size(300.dp).testTag("pager"), beyondViewportPageCount = 1) { page ->
                        val imageModifier = if (page == 0) {
                            Modifier.cloudMediaSharedElement("photo-$page", open.value, visibilityScope)
                        } else Modifier
                        Box(Modifier.size(300.dp).testTag("viewer-$page").then(imageModifier))
                    }
                }
            }
        }
        compose.mainClock.autoAdvance = false
        repeat(6) { cycle ->
            compose.runOnIdle { open.value = true }
            compose.mainClock.advanceTimeBy(if (cycle % 2 == 0) 1000 else 64)
            if (cycle % 2 == 0) {
                compose.onNodeWithTag("pager").performTouchInput { swipeLeft() }
                compose.mainClock.advanceTimeBy(1000)
            }
            compose.runOnIdle { open.value = false }
            compose.mainClock.advanceTimeBy(1000)
            compose.onNodeWithTag("pager").assertDoesNotExist()
        }
    }
}
