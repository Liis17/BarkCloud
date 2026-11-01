package com.barkfluff.BarkCloud.ui.components

import android.os.Build
import android.view.HapticFeedbackConstants
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.pulltorefresh.PullToRefreshState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.platform.LocalView
import kotlinx.coroutines.flow.distinctUntilChanged

/** One crisp pulse when the pull crosses the refresh threshold; never during refresh animation. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun PullRefreshHaptics(state: PullToRefreshState, refreshing: Boolean) {
    val view = LocalView.current
    val refreshingNow by rememberUpdatedState(refreshing)
    LaunchedEffect(state, view) {
        snapshotFlow { !refreshingNow && state.distanceFraction >= 1f }
            .distinctUntilChanged()
            .collect { ready ->
                if (ready) {
                    view.performHapticFeedback(
                        if (Build.VERSION.SDK_INT >= 34) HapticFeedbackConstants.GESTURE_THRESHOLD_ACTIVATE
                        else HapticFeedbackConstants.CONFIRM,
                    )
                }
            }
    }
}

/** Selection entry is firm; subsequent additions produce a light tick, including drag selection. */
@Composable
internal fun <K> SelectionHaptics(selected: Set<K>) {
    val view = LocalView.current
    val previous = remember(view) { SelectionSnapshot(selected) }
    LaunchedEffect(selected, view) {
        if (selected.any { it !in previous.keys }) {
            view.performHapticFeedback(
                if (previous.keys.isEmpty()) HapticFeedbackConstants.LONG_PRESS
                else if (Build.VERSION.SDK_INT >= 34) HapticFeedbackConstants.SEGMENT_TICK
                else HapticFeedbackConstants.CLOCK_TICK,
            )
        }
        previous.keys = selected
    }
}

private class SelectionSnapshot<K>(var keys: Set<K>)
