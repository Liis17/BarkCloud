package com.barkfluff.BarkCloud.ui.gallery

import kotlin.math.max

/**
 * Тайминги «волны» очистки (макет «C · Волна»): сетка едет вверх с ускорением,
 * ряд рассыпается, когда доезжает до верхнего края. Время — в секундах от старта.
 */
internal object CleanupWave {
    /** Экран «Готово» показывается не раньше этого момента. */
    const val DONE_SECONDS = 1.2f

    /** Профиль смещения макета (dp за 0..1 с шагом 0,1 с) для 8 рядов; используется в долях. */
    private val scrollKeys = floatArrayOf(0f, 31f, 78f, 141f, 221f, 316f, 428f, 555f, 699f, 859f, 1035f)
        .let { keys -> FloatArray(keys.size) { keys[it] / keys.last() } }

    /** Полёт частиц укорачивается по мере ускорения: (старт чётного ряда, длительность). */
    private val flightKeys = arrayOf(0f to 0.32f, 0.44f to 0.25f, 0.67f to 0.21f, 0.85f to 0.18f)

    /** Доля полного смещения сетки (0..1) к моменту [t]. */
    fun scrollFraction(t: Float): Float {
        if (t <= 0f) return 0f
        if (t >= 1f) return 1f
        val scaled = t * (scrollKeys.size - 1)
        val index = scaled.toInt()
        val local = scaled - index
        return scrollKeys[index] + (scrollKeys[index + 1] - scrollKeys[index]) * local
    }

    /** Момент, когда сетка смещается на долю [fraction]. */
    fun timeAtFraction(fraction: Float): Float {
        if (fraction <= 0f) return 0f
        if (fraction >= 1f) return 1f
        val step = 1f / (scrollKeys.size - 1)
        for (i in 1 until scrollKeys.size) {
            if (fraction <= scrollKeys[i]) {
                val local = (fraction - scrollKeys[i - 1]) / (scrollKeys[i] - scrollKeys[i - 1])
                return (i - 1 + local) * step
            }
        }
        return 1f
    }

    /**
     * Старт распада ряда [row] из [rows]: чётный ряд — когда доезжает до верха,
     * второй ряд пары — на середине полёта первого.
     */
    fun rowStart(row: Int, rows: Int): Float {
        val lead = row - row % 2
        val leadStart = timeAtFraction(lead.toFloat() / rows.coerceAtLeast(1))
        return if (row % 2 == 0) leadStart else leadStart + flightAfter(leadStart) / 2f
    }

    fun rowFlight(row: Int, rows: Int): Float =
        flightAfter(rowStart(row - row % 2, rows))

    /** Длительность всей анимации: до последней частицы, но не меньше [DONE_SECONDS]. */
    fun totalSeconds(rows: Int): Float =
        (0 until rows).fold(DONE_SECONDS) { acc, row -> max(acc, rowStart(row, rows) + rowFlight(row, rows)) }

    private fun flightAfter(start: Float): Float {
        if (start <= flightKeys.first().first) return flightKeys.first().second
        for (i in 1 until flightKeys.size) {
            val (t1, d1) = flightKeys[i]
            if (start <= t1) {
                val (t0, d0) = flightKeys[i - 1]
                return d0 + (d1 - d0) * (start - t0) / (t1 - t0)
            }
        }
        return flightKeys.last().second
    }
}
