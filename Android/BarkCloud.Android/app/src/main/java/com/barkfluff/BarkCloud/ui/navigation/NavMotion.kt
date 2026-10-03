package com.barkfluff.BarkCloud.ui.navigation

import androidx.compose.animation.AnimatedContentScope
import androidx.compose.animation.AnimatedContentTransitionScope
import androidx.compose.animation.EnterTransition
import androidx.compose.animation.ExitTransition
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.SpringSpec
import androidx.compose.animation.core.spring
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.scaleIn
import androidx.compose.animation.scaleOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.runtime.Composable
import androidx.navigation.NavBackStackEntry
import androidx.navigation.NavGraphBuilder
import androidx.navigation.NamedNavArgument
import androidx.navigation.compose.composable

// Токены движения Material 3 Expressive для навигации:
// spatial.default (stiffness 700, damping 0.9) — перемещения,
// effect.default (stiffness 1600, damping 1.0) — прозрачность.

private fun <T> navSpatial(): SpringSpec<T> = spring(
    dampingRatio = 0.9f,
    stiffness = 700f,
)

private fun <T> navEffect(): SpringSpec<T> = spring(
    dampingRatio = Spring.DampingRatioNoBouncy,
    stiffness = 1600f,
)

/** Shared Axis X, вход нового экрана (drill-in). */
val SharedAxisXEnter: AnimatedContentTransitionScope<NavBackStackEntry>.() -> EnterTransition = {
    slideInHorizontally(navSpatial()) { it / 4 } + fadeIn(navEffect())
}

/** Shared Axis X, уход старого экрана при drill-in (параллакс влево). */
val SharedAxisXExit: AnimatedContentTransitionScope<NavBackStackEntry>.() -> ExitTransition = {
    slideOutHorizontally(navSpatial()) { -it / 4 } + fadeOut(navEffect())
}

/** Shared Axis X, возврат при pop-back. */
val SharedAxisXPopEnter: AnimatedContentTransitionScope<NavBackStackEntry>.() -> EnterTransition = {
    slideInHorizontally(navSpatial()) { -it / 4 } + fadeIn(navEffect())
}

/** Shared Axis X, уход экрана при pop-back. */
val SharedAxisXPopExit: AnimatedContentTransitionScope<NavBackStackEntry>.() -> ExitTransition = {
    slideOutHorizontally(navSpatial()) { it / 4 } + fadeOut(navEffect())
}

/** Fade Through, вход — для переключения вкладок нижней навигации. */
val FadeThroughEnter: AnimatedContentTransitionScope<NavBackStackEntry>.() -> EnterTransition = {
    fadeIn(navEffect()) + scaleIn(navEffect(), initialScale = 0.92f)
}

/** Fade Through, выход. */
val FadeThroughExit: AnimatedContentTransitionScope<NavBackStackEntry>.() -> ExitTransition = {
    fadeOut(navEffect()) + scaleOut(navEffect(), targetScale = 0.92f)
}

/**
 * `composable` для иерархических переходов (детальный экран поверх списка):
 * shared axis X вместо дефолтного кроссфейда NavHost.
 */
fun NavGraphBuilder.drillIn(
    route: String,
    arguments: List<NamedNavArgument> = emptyList(),
    content: @Composable AnimatedContentScope.(NavBackStackEntry) -> Unit,
) {
    composable(
        route = route,
        arguments = arguments,
        enterTransition = SharedAxisXEnter,
        exitTransition = SharedAxisXExit,
        popEnterTransition = SharedAxisXPopEnter,
        popExitTransition = SharedAxisXPopExit,
        content = content,
    )
}
