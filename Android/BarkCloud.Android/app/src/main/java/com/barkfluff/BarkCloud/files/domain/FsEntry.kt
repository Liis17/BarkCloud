package com.barkfluff.BarkCloud.files.domain

sealed interface FsEntry {
    val path: String
    val name: String
    val lastModified: Long

    data class Directory(
        override val path: String,
        override val name: String,
        override val lastModified: Long,
        val childCount: Int,
    ) : FsEntry

    data class File(
        override val path: String,
        override val name: String,
        override val lastModified: Long,
        val sizeBytes: Long,
        val mimeType: String,
    ) : FsEntry
}

val FsEntry.isDirectory: Boolean
    get() = this is FsEntry.Directory
