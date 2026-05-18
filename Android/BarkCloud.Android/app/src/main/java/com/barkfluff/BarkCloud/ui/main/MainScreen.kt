package com.barkfluff.BarkCloud.ui.main

import android.net.Uri
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.CenterAlignedTopAppBar
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.navigation.NavType
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import androidx.navigation.navArgument
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.files.ui.FilesRootScreen
import com.barkfluff.BarkCloud.files.ui.LocalBrowserScreen
import com.barkfluff.BarkCloud.ui.screens.PlaceholderScreen

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MainScreen() {
    val navController = rememberNavController()
    val backStack by navController.currentBackStackEntryAsState()
    val currentRoute = backStack?.destination?.route

    // Скрываем общий TopAppBar на экранах раздела Files — у них собственный topBar.
    val showAppTopBar = currentRoute != "files/root" && currentRoute?.startsWith("files/local") != true

    Scaffold(
        topBar = {
            if (showAppTopBar) {
                CenterAlignedTopAppBar(
                    title = { Text(stringResource(R.string.app_name)) },
                )
            }
        },
        bottomBar = { MainBottomBar(navController) },
    ) { padding ->
        NavHost(
            navController = navController,
            startDestination = MainDestination.Default.route,
            modifier = Modifier.padding(padding),
        ) {
            composable(MainDestination.Photos.route) {
                PlaceholderScreen(destination = MainDestination.Photos)
            }
            composable(MainDestination.Videos.route) {
                PlaceholderScreen(destination = MainDestination.Videos)
            }
            composable(MainDestination.Files.route) {
                FilesRootScreen(
                    onOpenLocal = { path ->
                        val encoded = Uri.encode(path)
                        navController.navigate("files/local?path=$encoded")
                    },
                )
            }
            composable(
                route = "files/local?path={path}",
                arguments = listOf(navArgument("path") { type = NavType.StringType }),
            ) { entry ->
                val path = entry.arguments?.getString("path").orEmpty()
                LocalBrowserScreen(
                    initialPath = path,
                    onNavigateUp = { navController.popBackStack() },
                )
            }
            composable(MainDestination.Shared.route) {
                PlaceholderScreen(destination = MainDestination.Shared)
            }
            composable(MainDestination.Settings.route) {
                PlaceholderScreen(destination = MainDestination.Settings)
            }
        }
    }
}
