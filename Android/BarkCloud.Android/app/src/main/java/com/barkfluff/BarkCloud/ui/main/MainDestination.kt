package com.barkfluff.BarkCloud.ui.main

import androidx.annotation.StringRes
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Photo
import androidx.compose.material.icons.outlined.Search
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.material.icons.outlined.Photo
import androidx.compose.ui.graphics.vector.ImageVector
import com.barkfluff.BarkCloud.R

/** Четыре основные вкладки; маршруты gallery/albums сохраняют совместимость deep links. */
enum class MainDestination(
    val route: String,
    @StringRes val labelRes: Int,
    val iconOutlined: ImageVector,
    val iconFilled: ImageVector,
) {
    Gallery(
        route = "gallery",
        labelRes = R.string.tab_gallery,
        iconOutlined = Icons.Outlined.Photo,
        iconFilled = Icons.Filled.Photo,
    ),
    Albums(
        route = "albums",
        labelRes = R.string.tab_albums,
        iconOutlined = ShelvesOutlined,
        iconFilled = ShelvesFilled,
    ),
    Files(
        route = "files",
        labelRes = R.string.tab_files,
        iconOutlined = Icons.Outlined.Folder,
        iconFilled = Icons.Filled.Folder,
    ),
    Search(
        route = "search",
        labelRes = R.string.tab_search,
        iconOutlined = Icons.Outlined.Search,
        iconFilled = Icons.Filled.Search,
    );

    companion object {
        val Default = Gallery
    }
}
