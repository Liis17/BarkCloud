package com.barkfluff.BarkCloud.files.domain

enum class FsSort {
    NameAsc,
    NameDesc,
    SizeAsc,
    SizeDesc,
    DateAsc,
    DateDesc,
}

fun List<FsEntry>.applySort(sort: FsSort): List<FsEntry> {
    val (dirs, files) = partition { it is FsEntry.Directory }
    val dirComparator: Comparator<FsEntry> = compareBy(String.CASE_INSENSITIVE_ORDER) { it.name }
    val fileComparator: Comparator<FsEntry> = when (sort) {
        FsSort.NameAsc -> compareBy(String.CASE_INSENSITIVE_ORDER) { it.name }
        FsSort.NameDesc -> compareByDescending(String.CASE_INSENSITIVE_ORDER) { it.name }
        FsSort.SizeAsc -> compareBy { (it as? FsEntry.File)?.sizeBytes ?: 0L }
        FsSort.SizeDesc -> compareByDescending { (it as? FsEntry.File)?.sizeBytes ?: 0L }
        FsSort.DateAsc -> compareBy { it.lastModified }
        FsSort.DateDesc -> compareByDescending { it.lastModified }
    }
    return dirs.sortedWith(dirComparator) + files.sortedWith(fileComparator)
}
