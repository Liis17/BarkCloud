package com.barkfluff.BarkCloud.ui.theme

import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.SpringSpec
import androidx.compose.animation.core.spring

/**
 * Spring-токены Material 3 Expressive для собственных анимаций (выбор, морфинг форм,
 * перемещения). Компоненты M3 уже используют `MotionScheme.expressive()` из темы; эти
 * хелперы — для ручных `animate*AsState` в кастомных местах (бейдж выбора, FAB и т.п.).
 */
object BarkMotion {

    /** Перемещения с лёгким overshoot (выбор ячеек, появление элементов). */
    fun <T> spatial(): SpringSpec<T> = spring(
        dampingRatio = Spring.DampingRatioLowBouncy,
        stiffness = Spring.StiffnessMediumLow,
    )

    /** Эффекты без overshoot (прозрачность, цвет). */
    fun <T> effect(): SpringSpec<T> = spring(
        dampingRatio = Spring.DampingRatioNoBouncy,
        stiffness = Spring.StiffnessMedium,
    )
}
