package com.barkfluff.BarkCloud.ui.selection

import androidx.compose.foundation.gestures.scrollBy
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.grid.rememberLazyGridState
import androidx.compose.material3.Button
import androidx.compose.material3.Text
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.click
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.unit.dp
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Rule
import org.junit.Test

/**
 * Регрессия: обработчик тапа не должен захватываться первой композицией. Элементы грузятся асинхронно,
 * после выбора тап переключает выбор, после очистки снова открывает. Требует устройство или эмулятор.
 */
class DragSelectionTapUiTest {

    @get:Rule
    val rule = createComposeRule()

    @Test
    fun tapCallbackUsesLatestStateAfterAsyncLoadAndSelection() {
        val keys = (0 until 12).map { "k$it" }
        var loaded by mutableStateOf(emptyList<String>())
        var selection by mutableStateOf(emptySet<String>())
        var opened: String? = null

        rule.setContent {
            // Имитация асинхронной загрузки: до неё таблица поиска пуста.
            LaunchedEffect(Unit) { loaded = keys }
            val lookup = remember(loaded) { loaded.toSet() }
            val grid = rememberLazyGridState()
            val controller = rememberDragSelection(
                orderedKeys = loaded,
                selected = selection,
                onSelectionChange = { selection = it },
                visibleCells = { grid.visibleCells() },
                viewport = { grid.viewportBounds(bottomInsetPx = 0f) },
                scrollBy = { grid.scrollBy(it) },
                onDragActiveChange = {},
            )
            Column {
                LazyVerticalGrid(
                    columns = GridCells.Fixed(3),
                    state = grid,
                    modifier = Modifier.size(width = 300.dp, height = 300.dp).testTag("grid").dragSelection(controller) { key ->
                        if (key !in lookup) return@dragSelection
                        if (selection.isNotEmpty()) {
                            selection = selection.toggled(key)
                        } else {
                            opened = key
                        }
                    },
                ) {
                    items(loaded, key = { it }) { key -> Box(Modifier.aspectRatio(1f).testTag(key)) }
                }
                Button(onClick = { selection = emptySet() }, modifier = Modifier.testTag("clear")) { Text("clear") }
            }
        }
        rule.waitForIdle()
        val cell = with(rule.density) { 100.dp.toPx() }
        val row0 = Offset(0.5f * cell, 0.5f * cell)
        val row1 = Offset(1.5f * cell, 1.5f * cell)

        // 1. Тап по элементу без выбора — открыть.
        rule.onNodeWithTag("grid").performTouchInput { click(row0) }
        rule.waitForIdle()
        assertEquals("k0", opened)

        // 2. Долгое нажатие выбирает элемент.
        opened = null
        rule.onNodeWithTag("grid").performTouchInput {
            down(row1)
            advanceEventTime(1_000)
            up()
        }
        rule.waitForIdle()
        assertEquals(setOf("k4"), selection)

        // 3. Тап при активном выборе переключает выбор и не открывает просмотр.
        rule.onNodeWithTag("grid").performTouchInput { click(row0) }
        rule.waitForIdle()
        assertEquals(setOf("k4", "k0"), selection)
        assertNull(opened)

        // 4. Очистка выбора, затем тап снова открывает.
        rule.onNodeWithTag("clear").performClick()
        rule.waitForIdle()
        assertEquals(emptySet<String>(), selection)
        rule.onNodeWithTag("grid").performTouchInput { click(row0) }
        rule.waitForIdle()
        assertEquals("k0", opened)
    }
}
