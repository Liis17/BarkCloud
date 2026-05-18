package com.barkfluff.BarkCloud.ui.main

import androidx.annotation.StringRes
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Group
import androidx.compose.material.icons.filled.Movie
import androidx.compose.material.icons.filled.PhotoLibrary
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.material.icons.outlined.Group
import androidx.compose.material.icons.outlined.Movie
import androidx.compose.material.icons.outlined.PhotoLibrary
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.ui.graphics.vector.ImageVector
import com.barkfluff.BarkCloud.R

enum class MainDestination(
    val route: String,
    @StringRes val labelRes: Int,
    @StringRes val placeholderRes: Int,
    val iconOutlined: ImageVector,
    val iconFilled: ImageVector,
) {
    Photos(
        route = "photos",
        labelRes = R.string.tab_photos,
        placeholderRes = R.string.placeholder_photos,
        iconOutlined = Icons.Outlined.PhotoLibrary,
        iconFilled = Icons.Filled.PhotoLibrary,
    ),
    Videos(
        route = "videos",
        labelRes = R.string.tab_videos,
        placeholderRes = R.string.placeholder_videos,
        iconOutlined = Icons.Outlined.Movie,
        iconFilled = Icons.Filled.Movie,
    ),
    Files(
        route = "files",
        labelRes = R.string.tab_files,
        placeholderRes = R.string.placeholder_files,
        iconOutlined = Icons.Outlined.Folder,
        iconFilled = Icons.Filled.Folder,
    ),
    Shared(
        route = "shared",
        labelRes = R.string.tab_shared,
        placeholderRes = R.string.placeholder_shared,
        iconOutlined = Icons.Outlined.Group,
        iconFilled = Icons.Filled.Group,
    ),
    Settings(
        route = "settings",
        labelRes = R.string.tab_settings,
        placeholderRes = R.string.placeholder_settings,
        iconOutlined = Icons.Outlined.Settings,
        iconFilled = Icons.Filled.Settings,
    );

    companion object {
        val Default = Files
    }
}
