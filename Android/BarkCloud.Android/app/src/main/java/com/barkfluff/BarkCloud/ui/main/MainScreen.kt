package com.barkfluff.BarkCloud.ui.main

import android.net.Uri
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.Column
import androidx.compose.material3.Scaffold
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.navigation.NavType
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.navigation
import androidx.navigation.compose.rememberNavController
import androidx.navigation.navArgument
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.files.ui.FilesRootScreen
import com.barkfluff.BarkCloud.files.ui.LocalBrowserScreen
import com.barkfluff.BarkCloud.ui.albums.AlbumDetailScreen
import com.barkfluff.BarkCloud.ui.favorites.FavoritesScreen
import com.barkfluff.BarkCloud.ui.files.CloudBrowserScreen
import com.barkfluff.BarkCloud.ui.gallery.GalleryScreen
import com.barkfluff.BarkCloud.ui.media.MediaTabScreen
import com.barkfluff.BarkCloud.ui.settings.DevicesScreen
import com.barkfluff.BarkCloud.ui.shared.SharedFolderBrowserScreen
import com.barkfluff.BarkCloud.ui.shared.SharedHubScreen
import com.barkfluff.BarkCloud.ui.trash.TrashScreen
import com.barkfluff.BarkCloud.ui.settings.CacheSettingsScreen
import com.barkfluff.BarkCloud.ui.settings.EditProfileScreen
import com.barkfluff.BarkCloud.ui.settings.PrivacySettingsScreen
import com.barkfluff.BarkCloud.ui.settings.SettingsScreen
import com.barkfluff.BarkCloud.ui.settings.UploadSettingsScreen
import com.barkfluff.BarkCloud.ui.smartfolders.SmartFolderDetailScreen
import com.barkfluff.BarkCloud.ui.upload.GlobalUploadBanner
import com.barkfluff.BarkCloud.ui.upload.UploadQueueScreen
import com.barkfluff.BarkCloud.ui.upload.UploadQueueViewModel

/**
 * Главный экран с нижней навигацией из 5 вкладок (как в iOS). Каждая вкладка — свой
 * вложенный граф со своим back-stack (сохраняется через [MainBottomBar]). Реальные
 * экраны вкладок Галерея/Альбомы/Корзина/Настройки добавляются в фазе 4; пока — заглушки.
 */
@Composable
fun MainScreen(
    deepLink: Uri? = null,
    onSignOut: () -> Unit,
) {
    val navController = rememberNavController()
    val rootCloudTitle = stringResource(R.string.cloud_storage_title)
    val uploadViewModel: UploadQueueViewModel = viewModel(factory = UploadQueueViewModel.factory())
    val uploadState by uploadViewModel.state.collectAsStateWithLifecycle()

    LaunchedEffect(deepLink) {
        val target = deepLink?.host ?: return@LaunchedEffect
        val route = when (target) {
            "gallery" -> MainDestination.Gallery.route
            "files" -> MainDestination.Files.route
            "albums", "media" -> MainDestination.Albums.route
            "trash" -> MainDestination.Trash.route
            "settings" -> MainDestination.Settings.route
            else -> return@LaunchedEffect
        }
        navController.navigate(route) {
            launchSingleTop = true
            restoreState = true
        }
    }

    Scaffold(
        bottomBar = {
            Column {
                if (uploadState.isActive) {
                    GlobalUploadBanner(uploadState) { navController.navigate("uploads/root") }
                }
                MainBottomBar(navController)
            }
        },
    ) { padding ->
        NavHost(
            navController = navController,
            startDestination = MainDestination.Default.route,
            modifier = Modifier.padding(padding),
        ) {
            navigation(startDestination = "gallery/root", route = MainDestination.Gallery.route) {
                composable("gallery/root") {
                    GalleryScreen()
                }
            }

            navigation(startDestination = "files/root", route = MainDestination.Files.route) {
                composable("files/root") {
                    FilesRootScreen(
                        onOpenLocal = { path ->
                            navController.navigate("files/local?path=${Uri.encode(path)}")
                        },
                        onOpenCloud = {
                            val title = Uri.encode(rootCloudTitle)
                            navController.navigate("files/cloud?dir=&title=$title")
                        },
                        onOpenShared = { navController.navigate("files/shared") },
                        onOpenSmartFolder = { id, name ->
                            navController.navigate("files/smart/$id?title=${Uri.encode(name)}")
                        },
                    )
                }
                composable(
                    route = "files/local?path={path}",
                    arguments = listOf(navArgument("path") { type = NavType.StringType }),
                ) { entry ->
                    LocalBrowserScreen(
                        initialPath = entry.arguments?.getString("path").orEmpty(),
                        onNavigateUp = { navController.popBackStack() },
                    )
                }
                composable(
                    route = "files/cloud?dir={dir}&title={title}",
                    arguments = listOf(
                        navArgument("dir") { type = NavType.StringType; defaultValue = "" },
                        navArgument("title") { type = NavType.StringType; defaultValue = "" },
                    ),
                ) { entry ->
                    CloudBrowserScreen(
                        directoryId = entry.arguments?.getString("dir").orEmpty(),
                        title = entry.arguments?.getString("title").orEmpty().ifEmpty { rootCloudTitle },
                        onOpenFolder = { id, name ->
                            navController.navigate("files/cloud?dir=$id&title=${Uri.encode(name)}")
                        },
                        onNavigateUp = { navController.popBackStack() },
                    )
                }
                composable("files/shared") {
                    SharedHubScreen(
                        onOpenSharedFolder = { id, name ->
                            navController.navigate("files/shared/folder?dir=$id&title=${Uri.encode(name)}")
                        },
                    )
                }
                composable(
                    route = "files/shared/folder?dir={dir}&title={title}",
                    arguments = listOf(
                        navArgument("dir") { type = NavType.StringType },
                        navArgument("title") { type = NavType.StringType; defaultValue = "" },
                    ),
                ) { entry ->
                    SharedFolderBrowserScreen(
                        directoryId = entry.arguments?.getString("dir").orEmpty(),
                        title = entry.arguments?.getString("title").orEmpty(),
                        onOpenFolder = { id, name ->
                            navController.navigate("files/shared/folder?dir=$id&title=${Uri.encode(name)}")
                        },
                        onNavigateUp = { navController.popBackStack() },
                    )
                }
                composable(
                    route = "files/smart/{folderId}?title={title}",
                    arguments = listOf(
                        navArgument("folderId") { type = NavType.StringType },
                        navArgument("title") { type = NavType.StringType; defaultValue = "" },
                    ),
                ) { entry ->
                    SmartFolderDetailScreen(
                        folderId = entry.arguments?.getString("folderId").orEmpty(),
                        title = entry.arguments?.getString("title").orEmpty(),
                        onNavigateUp = { navController.popBackStack() },
                    )
                }
            }

            navigation(startDestination = "albums/root", route = MainDestination.Albums.route) {
                composable("albums/root") {
                    MediaTabScreen(
                        onOpenAlbum = { id, name ->
                            navController.navigate("albums/detail/$id?name=${Uri.encode(name)}")
                        },
                        onOpenFavorites = { navController.navigate("albums/favorites") },
                    )
                }
                composable(
                    route = "albums/detail/{albumId}?name={name}",
                    arguments = listOf(
                        navArgument("albumId") { type = NavType.StringType },
                        navArgument("name") { type = NavType.StringType; defaultValue = "" },
                    ),
                ) { entry ->
                    AlbumDetailScreen(
                        albumId = entry.arguments?.getString("albumId").orEmpty(),
                        albumName = entry.arguments?.getString("name").orEmpty(),
                        onNavigateUp = { navController.popBackStack() },
                    )
                }
                composable("albums/favorites") {
                    FavoritesScreen(onNavigateUp = { navController.popBackStack() })
                }
            }

            navigation(startDestination = "trash/root", route = MainDestination.Trash.route) {
                composable("trash/root") {
                    TrashScreen()
                }
            }

            navigation(startDestination = "settings/root", route = MainDestination.Settings.route) {
                composable("settings/root") {
                    SettingsScreen(
                        onEditProfile = { navController.navigate("settings/editProfile") },
                        onPrivacy = { navController.navigate("settings/privacy") },
                        onDevices = { navController.navigate("settings/devices") },
                        onUploadSettings = { navController.navigate("settings/uploads") },
                        onCache = { navController.navigate("settings/cache") },
                        onSignedOut = onSignOut,
                    )
                }
                composable("settings/editProfile") {
                    EditProfileScreen(onNavigateUp = { navController.popBackStack() })
                }
                composable("settings/privacy") {
                    PrivacySettingsScreen(onNavigateUp = { navController.popBackStack() })
                }
                composable("settings/devices") {
                    DevicesScreen(onNavigateUp = { navController.popBackStack() })
                }
                composable("settings/cache") {
                    CacheSettingsScreen(onNavigateUp = { navController.popBackStack() })
                }
                composable("settings/uploads") {
                    UploadSettingsScreen(onNavigateUp = { navController.popBackStack() })
                }
            }

            composable("uploads/root") {
                UploadQueueScreen(onNavigateUp = { navController.popBackStack() })
            }
        }
    }
}
