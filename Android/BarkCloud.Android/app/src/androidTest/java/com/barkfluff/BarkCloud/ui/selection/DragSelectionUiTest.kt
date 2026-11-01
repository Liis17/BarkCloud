package com.barkfluff.BarkCloud.ui.selection

import androidx.compose.foundation.gestures.scrollBy
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.grid.rememberLazyGridState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.unit.dp
import org.junit.Assert.assertEquals
import org.junit.Ignore
import org.junit.Rule
import org.junit.Test

/**
 * Жест на реальном UI: долгое нажатие на ячейку k7 и протягивание до k10. Сетка 300×600 dp, ячейки
 * 100 dp, жест идёт в середине области: краевая автопрокрутка не включается. Требует эмулятор
 * или устройство (`connectedDebugAndroidTest`); на JVM не запускается.
 */
class DragSelectionUiTest {

    @get:Rule
    val rule = createComposeRule()

    @Ignore("На эмуляторе API 36 зависает при протягивании ступенями (кадры остаются заморожены); покрытие — JVM-тесты DragSelectControllerTest")
    @Test
    fun longPressAndDragSelectsInclusiveRangeInDisplayOrder() {
        val keys = (0 until 30).map { "k$it" }
        var selection by mutableStateOf(emptySet<String>())
        rule.setContent {
            val grid = rememberLazyGridState()
            val controller = rememberDragSelection(
                orderedKeys = keys,
                selected = selection,
                onSelectionChange = { selection = it },
                visibleCells = { grid.visibleCells() },
                viewport = { grid.viewportBounds(bottomInsetPx = 0f) },
                scrollBy = { grid.scrollBy(it) },
                onDragActiveChange = {},
            )
            LazyVerticalGrid(
                columns = GridCells.Fixed(3),
                state = grid,
                modifier = Modifier.size(width = 300.dp, height = 600.dp).testTag("grid").dragSelection(controller) {},
            ) {
                items(keys, key = { it }) { key -> Box(Modifier.aspectRatio(1f).testTag(key)) }
            }
        }

        val cell = with(rule.density) { 100.dp.toPx() }
        // Центр k7 (вторая ячейка третьей строки) → центр k10 (вторая ячейка четвёртой строки).
        val from = Offset(1.5f * cell, 2.5f * cell)
        val to = Offset(1.5f * cell, 3.5f * cell)
        rule.onNodeWithTag("grid").performTouchInput {
            down(from)
            advanceEventTime(1_000)
            for (step in 1..10) {
                advanceEventTime(16)
                moveTo(from + (to - from) * (step / 10f))
            }
            up()
        }

        assertEquals(setOf("k7", "k8", "k9", "k10"), selection)
    }
}
