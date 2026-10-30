package com.barkfluff.BarkCloud.ui.gallery

// Each date heading spans the grid; media rows contain square cells.
internal fun galleryRemainingPx(
    sectionSizes: List<Int>,
    columns: Int,
    lastVisibleIndex: Int,
    lastVisibleBottomPx: Int,
    cellHeightPx: Int,
    headingHeightPx: Int,
): Int {
    var index = 0
    var remaining = lastVisibleBottomPx
    for (count in sectionSizes) {
        if (index > lastVisibleIndex) remaining += headingHeightPx
        index++
        val totalRows = (count + columns - 1) / columns
        val visibleRows = if (lastVisibleIndex < index) 0 else
            ((lastVisibleIndex - index) / columns + 1).coerceAtMost(totalRows)
        remaining += (totalRows - visibleRows) * cellHeightPx
        index += count
    }
    return remaining
}
