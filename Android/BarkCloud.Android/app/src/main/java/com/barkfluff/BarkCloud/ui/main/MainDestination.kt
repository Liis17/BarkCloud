package com.barkfluff.BarkCloud.ui.main

import androidx.annotation.StringRes
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.PhotoAlbum
import androidx.compose.material.icons.filled.PhotoLibrary
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.outlined.Delete
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.material.icons.outlined.PhotoAlbum
import androidx.compose.material.icons.outlined.PhotoLibrary
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.ui.graphics.vector.ImageVector
import com.barkfluff.BarkCloud.R

/**
 * Пять вкладок нижней навигации — точь-в-точь как в iOS-клиенте.
 * Порядок = порядок в таб-баре. По умолчанию открывается «Альбомы».
 * [route] — это маршрут вложенного навигационного графа вкладки.
 */
enum class MainDestination(
    val route: String,
    @StringRes val labelRes: Int,
    val iconOutlined: ImageVector,
    val iconFilled: ImageVector,
) {
    Gallery(
        route = "gallery",
        labelRes = R.string.tab_gallery,
        iconOutlined = Icons.Outlined.PhotoLibrary,
        iconFilled = Icons.Filled.PhotoLibrary,
    ),
    Files(
        route = "files",
        labelRes = R.string.tab_files,
        iconOutlined = Icons.Outlined.Folder,
        iconFilled = Icons.Filled.Folder,
    ),
    Albums(
        route = "albums",
        labelRes = R.string.tab_albums,
        iconOutlined = Icons.Outlined.PhotoAlbum,
        iconFilled = Icons.Filled.PhotoAlbum,
    ),
    Trash(
        route = "trash",
        labelRes = R.string.tab_trash,
        iconOutlined = Icons.Outlined.Delete,
        iconFilled = Icons.Filled.Delete,
    ),
    Settings(
        route = "settings",
        labelRes = R.string.tab_settings,
        iconOutlined = Icons.Outlined.Settings,
        iconFilled = Icons.Filled.Settings,
    );

    companion object {
        val Default = Albums
    }
}
