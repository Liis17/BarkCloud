package com.barkfluff.BarkCloud.ui.components

import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale

data class MediaDateSection<T>(val title: String, val items: List<T>)

fun <T> mediaDateSections(items: List<T>, timestampMillis: (T) -> Long): List<MediaDateSection<T>> {
    val zone = ZoneId.systemDefault()
    val today = LocalDate.now(zone)
    val formatter = DateTimeFormatter.ofPattern("d MMMM", Locale.getDefault())
    return items.groupBy { item ->
        Instant.ofEpochMilli(timestampMillis(item)).atZone(zone).toLocalDate()
    }.toSortedMap(compareByDescending { it }).map { (date, grouped) ->
        val title = when (date) {
            today -> "Сегодня"
            today.minusDays(1) -> "Вчера"
            else -> date.format(formatter)
        }
        MediaDateSection(title, grouped)
    }
}
