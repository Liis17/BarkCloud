package com.barkfluff.BarkCloud.ui.navigation

import android.net.Uri
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.ExitTransition
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.spring
import androidx.compose.animation.fadeIn
import androidx.compose.animation.scaleIn
import androidx.compose.foundation.layout.Box
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.platform.LocalContext
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.createSavedStateHandle
import androidx.lifecycle.viewmodel.CreationExtras
import androidx.lifecycle.viewmodel.compose.viewModel
import com.barkfluff.BarkCloud.data.OnboardingStore
import com.barkfluff.BarkCloud.grpc.ServerSettings
import com.barkfluff.BarkCloud.grpc.ReflectionServerConnectionProbe
import com.barkfluff.BarkCloud.ui.auth.RegistrationViewModel
import com.barkfluff.BarkCloud.ui.auth.ResetPasswordViewModel
import com.barkfluff.BarkCloud.ui.auth.RegistrationScreen
import com.barkfluff.BarkCloud.ui.auth.ResetPasswordScreen
import com.barkfluff.BarkCloud.ui.server.ServerScreen
import com.barkfluff.BarkCloud.ui.server.ServerViewModel
import com.barkfluff.BarkCloud.ui.onboarding.WelcomeScreen
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.rememberNavController
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.ui.applock.AppLockScreen
import com.barkfluff.BarkCloud.ui.login.LoginScreen
import com.barkfluff.BarkCloud.ui.main.MainScreen

private const val ROUTE_LOGIN = "login"
private const val ROUTE_MAIN = "main"
private const val ROUTE_WELCOME = "welcome"
private const val ROUTE_SERVER = "server"
private const val ROUTE_REGISTER = "register"
private const val ROUTE_RESET = "reset"

@Composable
fun RootNavGraph(deepLink: Uri? = null) {
    val context = LocalContext.current
    val app = context.applicationContext as BarkCloudApplication
    val sessionActive = app.globalParam.sessionActive.collectAsStateWithLifecycle()
    val onboarding = remember(app) { OnboardingStore(app) }
    val startDestination = remember(app) {
        val existing = ServerSettings.hasConfiguredServer || app.globalParam.hasValidRefreshToken()
        if (existing) onboarding.complete()
        when {
            app.globalParam.hasValidRefreshToken() -> ROUTE_MAIN
            app.authFlowRepository.pendingRegistration() != null -> ROUTE_REGISTER
            !onboarding.completed -> ROUTE_WELCOME
            ServerSettings.hasConfiguredServer -> ROUTE_LOGIN
            else -> ROUTE_SERVER
        }
    }
    val navController = rememberNavController()
    fun replaceRoot(route: String) {
        navController.navigate(route) {
            popUpTo(navController.graph.id) { inclusive = true }
            launchSingleTop = true
        }
    }
    fun returnToLogin() {
        if (!navController.popBackStack()) replaceRoot(ROUTE_LOGIN)
    }

    LaunchedEffect(navController) {
        // Saved navigation can predate a durable session/pending write or the onboarding migration.
        val restored = navController.currentDestination?.route
        if ((startDestination == ROUTE_MAIN && restored != ROUTE_MAIN) ||
            (startDestination == ROUTE_REGISTER && restored != ROUTE_REGISTER) ||
            (restored == ROUTE_WELCOME && startDestination != ROUTE_WELCOME)) {
            replaceRoot(startDestination)
        }
    }

    LaunchedEffect(sessionActive.value) {
        if (!sessionActive.value && navController.currentDestination?.route == ROUTE_MAIN) replaceRoot(ROUTE_LOGIN)
    }

    Box {
        NavHost(
            navController = navController,
            startDestination = startDestination,
            enterTransition = FadeThroughEnter,
            exitTransition = FadeThroughExit,
            popEnterTransition = FadeThroughEnter,
            popExitTransition = FadeThroughExit,
        ) {
            composable(ROUTE_WELCOME) {
                WelcomeScreen(onFinished = { onboarding.complete(); replaceRoot(ROUTE_SERVER) })
            }
            drillIn(ROUTE_SERVER) {
                val vm: ServerViewModel = viewModel(factory = flowFactory {
                    ServerViewModel(ServerSettings.config, ServerSettings.defaults, ReflectionServerConnectionProbe()) { config ->
                        if (config.key != ServerSettings.config.key) app.authFlowRepository.discardPendingRegistration()
                        ServerSettings.save(config)
                        app.grpcManager.shutdown()
                        com.barkfluff.BarkCloud.net.InsecureHttp.reset()
                    }
                })
                ServerScreen(vm, onConnected = { replaceRoot(ROUTE_LOGIN) },
                    onBack = if (ServerSettings.hasConfiguredServer) { { returnToLogin() } } else null)
            }
            composable(ROUTE_LOGIN) {
                LoginScreen(
                    server = ServerSettings.host,
                    onAuthenticated = { replaceRoot(ROUTE_MAIN) },
                    onRegister = { navController.navigate(ROUTE_REGISTER) },
                    onReset = { navController.navigate(ROUTE_RESET) },
                    onServer = { navController.navigate(ROUTE_SERVER) },
                    onResumeRegistration = if (app.authFlowRepository.pendingRegistration() != null) {
                        { navController.navigate(ROUTE_REGISTER) }
                    } else null,
                )
            }
            drillIn(ROUTE_REGISTER) {
                val vm: RegistrationViewModel = viewModel(factory = flowFactory {
                    RegistrationViewModel(app.authFlowRepository, it.createSavedStateHandle())
                })
                RegistrationScreen(vm, ServerSettings.host, onBack = { returnToLogin() }, onAuthenticated = { replaceRoot(ROUTE_MAIN) })
            }
            drillIn(ROUTE_RESET) {
                val vm: ResetPasswordViewModel = viewModel(factory = flowFactory {
                    ResetPasswordViewModel(app.authFlowRepository, it.createSavedStateHandle())
                })
                ResetPasswordScreen(vm, ServerSettings.host, onBack = { returnToLogin() }, onAuthenticated = { replaceRoot(ROUTE_MAIN) })
            }
            composable(ROUTE_MAIN) {
                MainScreen(
                    deepLink = deepLink,
                    onSignOut = { replaceRoot(ROUTE_LOGIN) },
                )
            }
        }

        val shouldShowLock by app.appLockManager.shouldShowLock.collectAsStateWithLifecycle()
        // Лок актуален только для аутентифицированной сессии — до логина показывать нечего.
        // Появление — быстрый fade+scale (effect-пружина), исчезновение мгновенное: это
        // security-оверлей, не допустимо показывать контент под ним дольше необходимого.
        AnimatedVisibility(
            visible = shouldShowLock && sessionActive.value,
            enter = fadeIn(spring(dampingRatio = Spring.DampingRatioNoBouncy, stiffness = Spring.StiffnessHigh)) +
                scaleIn(
                    animationSpec = spring(dampingRatio = Spring.DampingRatioNoBouncy, stiffness = Spring.StiffnessHigh),
                    initialScale = 0.96f,
                ),
            exit = ExitTransition.None,
        ) {
            AppLockScreen(onUnlocked = { app.appLockManager.unlock() })
        }
    }
}

private fun <VM : ViewModel> flowFactory(build: (CreationExtras) -> VM): ViewModelProvider.Factory =
    object : ViewModelProvider.Factory {
        @Suppress("UNCHECKED_CAST")
        override fun <T : ViewModel> create(modelClass: Class<T>, extras: CreationExtras): T = build(extras) as T
    }
