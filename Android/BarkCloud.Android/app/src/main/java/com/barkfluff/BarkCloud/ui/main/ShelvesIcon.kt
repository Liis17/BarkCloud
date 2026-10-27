package com.barkfluff.BarkCloud.ui.main

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.PathParser
import androidx.compose.ui.unit.dp

// Material Symbols, Apache-2.0: https://github.com/google/material-design-icons
// symbols/web/shelves/materialsymbolsoutlined/shelves[_fill1]_24px.svg
internal val ShelvesOutlined = shelvesIcon(
    "ShelvesOutlined",
    "M120-40v-880h80v80h560v-80h80v880h-80v-80H200v80h-80Z" +
        "m80-480h80v-160h240v160h240v-240H200v240Z" +
        "m0 320h240v-160h240v160h80v-240H200v240Z" +
        "m160-320h80v-80h-80v80Z" +
        "m160 320h80v-80h-80v80Z",
)

internal val ShelvesFilled = shelvesIcon(
    "ShelvesFilled",
    "M120-40v-880h80v80h560v-80h80v880h-80v-80H200v80h-80Z" +
        "m80-480h80v-160h240v160h240v-240H200v240Z" +
        "m0 320h240v-160h240v160h80v-240H200v240Z",
)

private fun shelvesIcon(name: String, path: String): ImageVector =
    ImageVector.Builder(name, 24.dp, 24.dp, 960f, 960f).apply {
        addGroup(translationY = 960f)
        addPath(PathParser().parsePathString(path).toNodes(), fill = SolidColor(Color.Black))
        clearGroup()
    }.build()
