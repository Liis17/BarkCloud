package com.barkfluff.BarkCloud.ui.selection

import androidx.compose.foundation.gestures.detectDragGesturesAfterLongPress
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.lazy.grid.LazyGridState
import androidx.compose.runtime.Composable
import com.barkfluff.BarkCloud.ui.components.SelectionHaptics
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.getValue
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.dp
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.pointer.pointerInput
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/**
 * Единственный владелец жестов выбора контейнера: долгое нажатие начинает диапазон, протягивание
 * расширяет или сужает его, у краёв включается автопрокрутка (и продолжается при неподвижном пальце).
 * Заголовки и пустые места не выбираются и не начинают жест.
 *
 * Состояние выбора хранит вызывающий экран: контроллер получает его через [selected] и отдаёт
 * результат через [applySelection].
 */
internal class DragSelectController<K>(
    private val scope: CoroutineScope,
    private val orderedKeys: () -> List<K>,
    private val cells: () -> List<HitCell<K>>,
    /** Верх и низ области автопрокрутки в координатах контейнера (учитывают плавающую панель). */
    private val viewport: () -> Pair<Float, Float>,
    private val selected: () -> Set<K>,
    private val applySelection: (Set<K>) -> Unit,
    private val scrollBy: suspend (Float) -> Unit,
    private val onActiveChange: (Boolean) -> Unit,
    private val edgePx: Float,
    private val maxStepPx: Float,
    private val frameDelayMillis: Long = 16L,
) {
    private var drag: RangeDrag<K>? = null
    private var pointerX = 0f
    private var pointerY = 0f
    private var autoScroll: Job? = null

    val isDragging: Boolean get() = drag != null

    /** Ключ выбираемой ячейки под точкой. Заголовки, разделители и пустые места дают null. */
    fun keyAt(x: Float, y: Float): K? = hitKey(cells(), x, y)

    /** Начинает жест с ячейки под точкой. Возвращает false, если под пальцем нет выбираемого элемента. */
    fun start(x: Float, y: Float): Boolean {
        val key = keyAt(x, y) ?: return false
        val next = RangeDrag(base = selected(), anchor = key, current = key)
        drag = next
        pointerX = x
        pointerY = y
        applySelection(next.selection(orderedKeys()))
        onActiveChange(true)
        autoScroll?.cancel()
        autoScroll = scope.launch {
            while (isActive && drag != null) {
                tick()
                delay(frameDelayMillis)
            }
        }
        return true
    }

    fun move(x: Float, y: Float) {
        pointerX = x
        pointerY = y
        updateRange()
    }

    fun end() {
        if (drag == null) return
        drag = null
        autoScroll?.cancel()
        autoScroll = null
        onActiveChange(false)
    }

    private suspend fun tick() {
        val (top, bottom) = viewport()
        val step = autoScrollStep(pointerY, top, bottom, edgePx, maxStepPx)
        if (step != 0f) scrollBy(step)
        updateRange()
    }

    private fun updateRange() {
        val current = drag ?: return
        val order = orderedKeys()
        if (current.anchor !in order) {
            // Якорь исчез (элемент удалён или список перестроен): жест завершается без изменений.
            end()
            return
        }
        val (top, bottom) = viewport()
        val key = hitKeyClamped(cells(), pointerX, pointerY, top, bottom) ?: return
        val next = current.copy(current = key)
        drag = next
        applySelection(next.selection(order))
    }
}

/**
 * Точка за пределами видимой области (например, над экраном при автопрокрутке) относится к крайней
 * видимой строке, чтобы диапазон дотягивался до видимого края. Внутри области промежутки игнорируются.
 */
internal fun <K> hitKeyClamped(cells: List<HitCell<K>>, x: Float, y: Float, top: Float, bottom: Float): K? {
    hitKey(cells, x, y)?.let { return it }
    if (cells.isEmpty()) return null
    return when {
        y < top -> {
            val row = cells.minOf { it.top }
            cells.filter { it.top == row }.minBy { kotlin.math.abs((it.left + it.right) / 2f - x) }.key
        }
        y >= bottom -> {
            val row = cells.maxOf { it.bottom }
            cells.filter { it.bottom == row }.minBy { kotlin.math.abs((it.left + it.right) / 2f - x) }.key
        }
        else -> null
    }
}

/**
 * Жесты контейнера: короткое нажатие (открыть либо переключить при выборе) и долгое нажатие с
 * протягиванием. Долгое нажатие и протягивание не должны прокручивать список параллельно: экран
 * отключает пользовательскую прокрутку на время жеста ([DragSelectController.isDragging]).
 */
@Composable
internal fun <K> Modifier.dragSelection(
    controller: DragSelectController<K>,
    onTap: (K) -> Unit,
): Modifier {
    // Контроллер стабилен, поэтому жесты читают актуальный onTap на каждом касании, а не первый захваченный.
    val latestTap by rememberUpdatedState(onTap)
    return this
        .pointerInput(controller) {
            detectTapGestures(onTap = { offset ->
                controller.keyAt(offset.x, offset.y)?.let { key -> latestTap(key) }
            })
        }
        .pointerInput(controller) {
            detectDragGesturesAfterLongPress(
                onDragStart = { offset -> controller.start(offset.x, offset.y) },
                onDrag = { change, _ ->
                    change.consume()
                    controller.move(change.position.x, change.position.y)
                },
                onDragEnd = { controller.end() },
                onDragCancel = { controller.end() },
            )
        }
}
/** Видимые выбираемые ячейки в координатах контейнера. */
@Suppress("UNCHECKED_CAST")
internal fun <K> List<VisibleCellInfo>.toHitCells(selectable: Set<K>): List<HitCell<K>> = mapNotNull { info ->
    val key = (info.key as? K)?.takeIf { it in selectable } ?: return@mapNotNull null
    HitCell(key, info.left, info.top, info.right, info.bottom)
}

/** Геометрия видимого элемента ленивого списка или сетки (без привязки к типу layout-info). */
internal data class VisibleCellInfo(val key: Any?, val left: Float, val top: Float, val right: Float, val bottom: Float)


/** Ячейки видимой сетки в координатах контейнера (тот же узел, что и [LazyVerticalGrid]). */
internal fun LazyGridState.visibleCells(): List<VisibleCellInfo> = layoutInfo.visibleItemsInfo.map { info ->
    VisibleCellInfo(
        key = info.key,
        left = info.offset.x.toFloat(),
        top = info.offset.y.toFloat(),
        right = (info.offset.x + info.size.width).toFloat(),
        bottom = (info.offset.y + info.size.height).toFloat(),
    )
}

/** Ячейки видимого списка строк (Корзина): ширина — на всю область. */
internal fun LazyListState.visibleRows(): List<VisibleCellInfo> {
    val width = layoutInfo.viewportSize.width.toFloat()
    return layoutInfo.visibleItemsInfo.map { info ->
        VisibleCellInfo(
            key = info.key,
            left = 0f,
            top = info.offset.toFloat(),
            right = width,
            bottom = (info.offset + info.size).toFloat(),
        )
    }
}

/**
 * Контроллер выбора для экрана. [orderedKeys] — выбираемые ключи в порядке отображения, [visibleCells]
 * — текущая геометрия видимых элементов, [scrollBy] — прокрутка списка на шаг.
 */
@Composable
internal fun <K> rememberDragSelection(
    orderedKeys: List<K>,
    selected: Set<K>,
    onSelectionChange: (Set<K>) -> Unit,
    visibleCells: () -> List<VisibleCellInfo>,
    viewport: () -> Pair<Float, Float>,
    scrollBy: suspend (Float) -> Unit,
    onDragActiveChange: (Boolean) -> Unit,
): DragSelectController<K> {
    val scope = rememberCoroutineScope()
    SelectionHaptics(selected)
    val density = LocalDensity.current
    val edgePx = with(density) { AUTO_SCROLL_EDGE.toPx() }
    val maxStepPx = with(density) { AUTO_SCROLL_MAX_STEP.toPx() }
    val keys by rememberUpdatedState(orderedKeys)
    val selectedNow by rememberUpdatedState(selected)
    val changeSelection by rememberUpdatedState(onSelectionChange)
    val cells by rememberUpdatedState(visibleCells)
    val scrollStep by rememberUpdatedState(scrollBy)
    val viewportNow by rememberUpdatedState(viewport)
    val dragChanged by rememberUpdatedState(onDragActiveChange)
    return remember(scope) {
        DragSelectController(
            scope = scope,
            orderedKeys = { keys },
            cells = { cells().toHitCells(keys.toSet()) },
            viewport = { viewportNow() },
            selected = { selectedNow },
            applySelection = { changeSelection(it) },
            scrollBy = { scrollStep(it) },
            onActiveChange = { dragChanged(it) },
            edgePx = edgePx,
            maxStepPx = maxStepPx,
        )
    }
}

/** Ширина зоны автопрокрутки у края и максимальный шаг за кадр. */
internal val AUTO_SCROLL_EDGE = 72.dp
internal val AUTO_SCROLL_MAX_STEP = 24.dp

/** Верх и низ области автопрокрутки сетки; [bottomInsetPx] — панель выбора, закрывающая низ. */
internal fun LazyGridState.viewportBounds(bottomInsetPx: Float): Pair<Float, Float> =
    layoutInfo.viewportStartOffset.toFloat() to (layoutInfo.viewportEndOffset - bottomInsetPx)

/** Верх и низ области автопрокрутки списка; [bottomInsetPx] — панель выбора, закрывающая низ. */
internal fun LazyListState.viewportBounds(bottomInsetPx: Float): Pair<Float, Float> =
    layoutInfo.viewportStartOffset.toFloat() to (layoutInfo.viewportEndOffset - bottomInsetPx)
