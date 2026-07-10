package com.barkfluff.BarkCloud.ui.navigation

import android.net.Uri
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.remember
import androidx.compose.ui.platform.LocalContext
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.rememberNavController
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.ui.login.LoginScreen
import com.barkfluff.BarkCloud.ui.main.MainScreen

private const val ROUTE_LOGIN = "login"
private const val ROUTE_MAIN = "main"

@Composable
fun RootNavGraph(deepLink: Uri? = null) {
    val context = LocalContext.current
    val app = context.applicationContext as BarkCloudApplication
    val sessionActive = app.globalParam.sessionActive.collectAsStateWithLifecycle()
    val startDestination = remember(app) {
        if (app.globalParam.hasValidRefreshToken()) ROUTE_MAIN else ROUTE_LOGIN
    }
    val navController = rememberNavController()

    LaunchedEffect(sessionActive.value) {
        if (!sessionActive.value) {
            navController.navigate(ROUTE_LOGIN) {
                popUpTo(ROUTE_MAIN) { inclusive = true }
                launchSingleTop = true
            }
        }
    }

    NavHost(
        navController = navController,
        startDestination = startDestination,
    ) {
        composable(ROUTE_LOGIN) {
            LoginScreen(
                onAuthenticated = {
                    navController.navigate(ROUTE_MAIN) {
                        popUpTo(ROUTE_LOGIN) { inclusive = true }
                        launchSingleTop = true
                    }
                },
            )
        }
        composable(ROUTE_MAIN) {
            MainScreen(
                deepLink = deepLink,
                onSignOut = {
                    navController.navigate(ROUTE_LOGIN) {
                        popUpTo(ROUTE_MAIN) { inclusive = true }
                        launchSingleTop = true
                    }
                },
            )
        }
    }
}
