package com.barkfluff.BarkCloud.files.ui

import android.content.Context
import com.barkfluff.BarkCloud.R
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

fun formatSize(context: Context, bytes: Long): String = when {
    bytes < 1024 -> context.getString(R.string.files_size_b, bytes)
    bytes < 1024L * 1024 -> context.getString(R.string.files_size_kb, bytes / 1024.0)
    bytes < 1024L * 1024 * 1024 -> context.getString(R.string.files_size_mb, bytes / (1024.0 * 1024.0))
    else -> context.getString(R.string.files_size_gb, bytes / (1024.0 * 1024.0 * 1024.0))
}

fun formatChildCount(context: Context, count: Int): String = when {
    count == 0 -> context.getString(R.string.files_items_count_zero)
    count % 10 == 1 && count % 100 != 11 -> context.getString(R.string.files_items_count_one, count)
    count % 10 in 2..4 && count % 100 !in 12..14 -> context.getString(R.string.files_items_count_few, count)
    else -> context.getString(R.string.files_items_count_many, count)
}

private val dateFormat = SimpleDateFormat("dd.MM.yyyy", Locale.getDefault())

fun formatDate(epochMillis: Long): String = dateFormat.format(Date(epochMillis))
