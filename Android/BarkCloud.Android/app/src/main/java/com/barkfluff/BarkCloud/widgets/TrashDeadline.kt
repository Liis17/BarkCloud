package com.barkfluff.BarkCloud.widgets

private const val HOUR_MILLIS = 60 * 60 * 1000L
private const val DAY_MILLIS = 24 * HOUR_MILLIS

/** Срок до авто-удаления самого старого элемента корзины, в единицах для показа. */
sealed interface TrashDeadline {
    data object Soon : TrashDeadline
    data class Hours(val hours: Int) : TrashDeadline
    data class Days(val days: Int) : TrashDeadline
}

/** Меньше часа — «скоро», меньше суток — часы, иначе — целые дни (вниз). */
fun trashDeadline(purgeAtMillis: Long, nowMillis: Long): TrashDeadline {
    val remaining = purgeAtMillis - nowMillis
    return when {
        remaining < HOUR_MILLIS -> TrashDeadline.Soon
        remaining < DAY_MILLIS -> TrashDeadline.Hours((remaining / HOUR_MILLIS).toInt())
        else -> TrashDeadline.Days((remaining / DAY_MILLIS).toInt())
    }
}
