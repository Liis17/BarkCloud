package com.barkfluff.BarkCloud.ui.selection

/** Видимая выбираемая ячейка в координатах контейнера жеста (правая/нижняя граница не включается). */
internal data class HitCell<K>(val key: K, val left: Float, val top: Float, val right: Float, val bottom: Float)

/**
 * Состояние одного жеста «долгое нажатие и протягивание». [base] — выбор до жеста (снимок),
 * [anchor] — ключ, с которого начали, [current] — ключ под пальцем.
 */
internal data class RangeDrag<K>(val base: Set<K>, val anchor: K, val current: K) {
    /** Выбор: снимок до жеста плюс отрезок от anchor до current в порядке отображения, включительно. */
    fun selection(orderedKeys: List<K>): Set<K> = rangeSelection(orderedKeys, base, anchor, current)
}

/**
 * Снимок [base] плюс включительный отрезок между [anchor] и [current] по порядку [orderedKeys].
 * Ключи вне отрезка из [base] сохраняются. Если якорь пропал из списка, вызывающий завершает жест.
 */
internal fun <K> rangeSelection(orderedKeys: List<K>, base: Set<K>, anchor: K, current: K): Set<K> {
    val from = orderedKeys.indexOf(anchor)
    val to = orderedKeys.indexOf(current)
    if (from < 0 || to < 0) return base + anchor
    val range = orderedKeys.subList(minOf(from, to), maxOf(from, to) + 1)
    return base + range
}

/** Ключ ячейки под точкой или null (заголовки, разделители и пустые места игнорируются). */
internal fun <K> hitKey(cells: List<HitCell<K>>, x: Float, y: Float): K? =
    cells.firstOrNull { x >= it.left && x < it.right && y >= it.top && y < it.bottom }?.key

/**
 * Шаг автопрокрутки по вертикали для указателя [pointerY] внутри области [top]..[bottom].
 * У верхней и нижней кромки шаг растёт линейно от 0 до [maxStepPx]; за пределами области — максимум.
 * Отрицательный шаг — вверх.
 */
internal fun autoScrollStep(pointerY: Float, top: Float, bottom: Float, edgePx: Float, maxStepPx: Float): Float {
    if (edgePx <= 0f) return 0f
    val upperZone = top + edgePx
    val lowerZone = bottom - edgePx
    return when {
        pointerY < upperZone -> -maxStepPx * ((upperZone - pointerY) / edgePx).coerceIn(0f, 1f)
        pointerY > lowerZone -> maxStepPx * ((pointerY - lowerZone) / edgePx).coerceIn(0f, 1f)
        else -> 0f
    }
}

/** Переключает ключ в выборе: выбранный снимается, невыбранный добавляется. */
internal fun <K> Set<K>.toggled(key: K): Set<K> = if (key in this) this - key else this + key
