package com.barkfluff.BarkCloud.ui.auth.components

import android.database.ContentObserver
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import androidx.compose.animation.core.*
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asComposePath
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.drawscope.scale
import androidx.compose.ui.graphics.drawscope.translate
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.graphics.shapes.*
import androidx.graphics.shapes.toPath as shapePath
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import kotlinx.coroutines.delay
import kotlin.math.PI
import kotlin.math.sin

enum class AuthDecorationKind { CLOUD, PHOTOS, SHARE, SERVER, PERSON, USERNAME, PASSWORD, MAIL, RESET, SUCCESS }

@Composable
fun rememberAuthMotionAllowed(): Boolean {
    val context = LocalContext.current
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    fun enabled() = Settings.Global.getFloat(context.contentResolver, Settings.Global.ANIMATOR_DURATION_SCALE, 1f) > 0f
    var animations by remember { mutableStateOf(enabled()) }
    var resumed by remember { mutableStateOf(lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) }
    DisposableEffect(context, lifecycle) {
        val observer = object : ContentObserver(Handler(Looper.getMainLooper())) {
            override fun onChange(selfChange: Boolean) { animations = enabled() }
        }
        context.contentResolver.registerContentObserver(Settings.Global.getUriFor(Settings.Global.ANIMATOR_DURATION_SCALE), false, observer)
        val lifecycleObserver = LifecycleEventObserver { _, _ -> resumed = lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED) }
        lifecycle.addObserver(lifecycleObserver)
        onDispose { context.contentResolver.unregisterContentObserver(observer); lifecycle.removeObserver(lifecycleObserver) }
    }
    return animations && resumed
}

@Composable
fun AuthHero(title: String, step: String?, kind: AuthDecorationKind, height: Dp, onBack: (() -> Unit)?, motion: Boolean) {
    val colors = MaterialTheme.colorScheme
    val largeText = LocalDensity.current.fontScale > 1.3f
    BoxWithConstraints(Modifier.fillMaxWidth().heightIn(min = height)
        .clip(RoundedCornerShape(bottomStart = 40.dp, bottomEnd = 40.dp)).background(colors.tertiaryContainer)) {
        val availableWidth = maxWidth
        if (!largeText) AuthDecoration(kind, motion, Modifier.align(Alignment.CenterEnd).padding(end = 24.dp, top = 12.dp).size(110.dp))
        Column(Modifier.fillMaxWidth().heightIn(min = height).padding(start = 24.dp, end = 24.dp, bottom = 28.dp, top = 8.dp), verticalArrangement = Arrangement.SpaceBetween) {
            if (onBack != null) IconButton(onBack, Modifier.offset(x = (-12).dp)) {
                Icon(Icons.AutoMirrored.Outlined.ArrowBack, "Назад", tint = colors.onTertiaryContainer)
            } else Spacer(Modifier.height(48.dp))
            Spacer(Modifier.height(20.dp))
            Column(Modifier.width(if (largeText) availableWidth - 48.dp else (availableWidth - 48.dp) * .66f)) {
                if (step != null) Text(step, style = MaterialTheme.typography.labelLarge, color = colors.onTertiaryContainer, modifier = Modifier.padding(bottom = 4.dp))
                Text(title, style = MaterialTheme.typography.headlineLarge, fontWeight = FontWeight.Bold,
                    color = colors.onTertiaryContainer, modifier = Modifier.semantics { heading() })
            }
        }
    }
}

@Composable
fun AuthDecoration(kind: AuthDecorationKind, motion: Boolean, modifier: Modifier = Modifier, page: Int? = null, stagger: Int = 0) {
    val colors = MaterialTheme.colorScheme
    val phase: State<Float> = if (motion) rememberInfiniteTransition(label = "decor float").animateFloat(
        0f, 1f, infiniteRepeatable(tween(8000 + stagger * 1000, easing = LinearEasing)), label = "decor phase"
    ) else rememberUpdatedState(0f)
    val entrance = remember(kind) { Animatable(if (motion) 0f else 1f) }
    LaunchedEffect(kind, motion) {
        if (motion) { delay(stagger * 50L); entrance.animateTo(1f, spring(.8f, 400f)) }
        else entrance.snapTo(1f)
    }
    val shapes = remember {
        listOf(RoundedPolygon.star(8, innerRadius = .78f, rounding = CornerRounding(.16f)),
            RoundedPolygon.star(12, innerRadius = .83f, rounding = CornerRounding(.10f)),
            RoundedPolygon.star(5, innerRadius = .62f, rounding = CornerRounding(.20f)))
    }
    val morphs = remember { listOf(Morph(shapes[0], shapes[1]), Morph(shapes[1], shapes[2])) }
    val pagePosition = animateFloatAsState((page ?: when (kind) {
        AuthDecorationKind.PHOTOS, AuthDecorationKind.USERNAME, AuthDecorationKind.SUCCESS -> 1
        AuthDecorationKind.SHARE, AuthDecorationKind.MAIL -> 2
        else -> 0
    }).toFloat(), spring(.9f, 700f), label = "decor morph")
    val blob = remember { RoundedPolygon(5, rounding = CornerRounding(.5f)) }
    val icon = when (kind) {
        AuthDecorationKind.CLOUD -> Icons.Outlined.Cloud
        AuthDecorationKind.PHOTOS -> Icons.Outlined.Photo
        AuthDecorationKind.SHARE -> Icons.Outlined.Share
        AuthDecorationKind.SERVER -> Icons.Outlined.Dns
        AuthDecorationKind.PERSON -> Icons.Outlined.Person
        AuthDecorationKind.USERNAME -> Icons.Outlined.AlternateEmail
        AuthDecorationKind.PASSWORD -> Icons.Outlined.Lock
        AuthDecorationKind.MAIL -> Icons.Outlined.Mail
        AuthDecorationKind.RESET -> Icons.Outlined.LockReset
        AuthDecorationKind.SUCCESS -> Icons.Outlined.Check
    }
    Box(modifier.clearAndSetSemantics {}.graphicsLayer {
        val wave = sin(phase.value * 2f * PI.toFloat())
        translationY = if (motion) wave * 6.dp.toPx() else 0f
        rotationZ = if (motion) wave * 5f else 0f
        alpha = entrance.value.coerceIn(0f, 1f)
        scaleX = .9f + .1f * entrance.value
        scaleY = scaleX
    }, contentAlignment = Alignment.Center) {
        Canvas(Modifier.fillMaxSize()) {
            val progress = pagePosition.value.coerceIn(0f, 2f)
            val path = if (page == null && kind in setOf(AuthDecorationKind.PERSON, AuthDecorationKind.RESET)) blob.shapePath().asComposePath()
                else morphs[if (progress < 1f) 0 else 1].shapePath(if (progress < 1f) progress else progress - 1f).asComposePath()
            translate(size.width / 2, size.height / 2) {
                scale(size.minDimension * .46f, size.minDimension * .46f, pivot = androidx.compose.ui.geometry.Offset.Zero) {
                    drawPath(path, colors.primary)
                }
            }
        }
        Icon(icon, null, tint = colors.onPrimary, modifier = Modifier.fillMaxSize(.42f))
    }
}

@Composable
private fun floatingAccent(modifier: Modifier, motion: Boolean, index: Int): Modifier {
    val entrance = remember { Animatable(if (motion) 0f else 1f) }
    val phase = if (motion) rememberInfiniteTransition(label = "accent float").animateFloat(
        0f, 1f, infiniteRepeatable(tween(8000 + index * 1000, easing = LinearEasing)), label = "accent phase"
    ) else rememberUpdatedState(0f)
    LaunchedEffect(motion) {
        if (motion) { delay(index * 50L); entrance.animateTo(1f, spring(.8f, 400f)) } else entrance.snapTo(1f)
    }
    return modifier.graphicsLayer {
        alpha = entrance.value.coerceIn(0f, 1f)
        scaleX = .9f + .1f * entrance.value; scaleY = scaleX
        val wave = sin(phase.value * 2f * PI.toFloat())
        translationY = if (motion) wave * 6.dp.toPx() else 0f
        rotationZ = if (motion) wave * 5f else 0f
    }
}

@Composable
fun WelcomeArt(page: Int, motion: Boolean, modifier: Modifier = Modifier) {
    val colors = MaterialTheme.colorScheme
    val kinds = listOf(AuthDecorationKind.CLOUD, AuthDecorationKind.PHOTOS, AuthDecorationKind.SHARE)
    BoxWithConstraints(modifier.fillMaxWidth().clip(RoundedCornerShape(bottomStart = 48.dp, bottomEnd = 48.dp))
        .background(colors.tertiaryContainer).clearAndSetSemantics {}) {
        if (maxHeight < 180.dp) {
            Row(Modifier.fillMaxSize().padding(16.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.SpaceBetween) {
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Icon(Icons.Outlined.Cloud, null, tint = colors.onTertiaryContainer)
                    Text("BarkCloud", style = MaterialTheme.typography.titleLarge, color = colors.onTertiaryContainer, fontWeight = FontWeight.SemiBold)
                }
                AuthDecoration(kinds[page], motion, Modifier.size(48.dp), page)
            }
        } else {
            Row(Modifier.padding(24.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Icon(Icons.Outlined.Cloud, null, tint = colors.onTertiaryContainer)
                Text("BarkCloud", style = MaterialTheme.typography.titleLarge, color = colors.onTertiaryContainer, fontWeight = FontWeight.SemiBold)
            }
            AuthDecoration(kinds[page], motion, Modifier.align(Alignment.Center).size((maxHeight * .48f).coerceAtMost(200.dp)), page)
            AuthDecoration(if (page == 1) AuthDecorationKind.PHOTOS else AuthDecorationKind.CLOUD, motion,
                Modifier.align(Alignment.BottomStart).padding(start = 20.dp, bottom = maxHeight * .18f).size(86.dp), stagger = 1)
            Surface(floatingAccent(Modifier.align(Alignment.TopEnd).padding(end = 24.dp, top = maxHeight * .18f).size(68.dp), motion, 2),
                shape = RoundedCornerShape(34.dp), color = colors.surface) {
                Box(contentAlignment = Alignment.Center) {
                    Icon(if (page == 2) Icons.Outlined.Link else Icons.Outlined.Folder, null, tint = colors.primary, modifier = Modifier.size(30.dp))
                }
            }
            Surface(Modifier.align(Alignment.BottomEnd).padding(end = 28.dp, bottom = 32.dp).graphicsLayer { rotationZ = -8f },
                shape = RoundedCornerShape(28.dp), color = colors.secondaryContainer) {
                Row(Modifier.padding(horizontal = 18.dp, vertical = 14.dp), verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Icon(if (page == 2) Icons.Outlined.OfflinePin else Icons.Outlined.Sync, null, tint = colors.onSecondaryContainer)
                    Text(listOf("Синхронизация", "Автозагрузка", "Офлайн")[page], color = colors.onSecondaryContainer, style = MaterialTheme.typography.labelLarge)
                }
            }
        }
    }
}

@Composable
fun AuthSuccessScreen(name: String, username: String, server: String, onContinue: () -> Unit) {
    val motion = rememberAuthMotionAllowed()
    val burst = remember { Animatable(if (motion) 0f else 1f) }
    LaunchedEffect(motion) { if (motion) burst.animateTo(1f, tween(800, easing = FastOutSlowInEasing)) else burst.snapTo(1f) }
    val colors = MaterialTheme.colorScheme
    BoxWithConstraints(Modifier.fillMaxSize().background(colors.surface).windowInsetsPadding(WindowInsets.safeDrawing)) {
        val availableHeight = maxHeight
        Column(Modifier.widthIn(max = 480.dp).fillMaxWidth().align(Alignment.TopCenter).verticalScroll(androidx.compose.foundation.rememberScrollState())) {
            Box(Modifier.fillMaxWidth().height(if (availableHeight < 650.dp) 260.dp else 400.dp)
                .clip(RoundedCornerShape(bottomStart = 48.dp, bottomEnd = 48.dp)).background(colors.tertiaryContainer)) {
                Canvas(Modifier.fillMaxSize().clearAndSetSemantics {}) {
                    val positions = listOf(.12f to .18f, .2f to .75f, .88f to .2f, .75f to .8f, .42f to .1f, .9f to .54f, .08f to .46f, .44f to .88f)
                    positions.forEachIndexed { index, (x, y) ->
                        val p = burst.value
                        val point = androidx.compose.ui.geometry.Offset(size.width * (.5f + (x - .5f) * p), size.height * (.5f + (y - .5f) * p))
                        drawCircle(listOf(colors.primary, colors.secondary, colors.surface)[index % 3], (5 + index % 3 * 3).dp.toPx(), point, alpha = p)
                    }
                }
                AuthDecoration(AuthDecorationKind.SUCCESS, false, Modifier.align(Alignment.Center).size(180.dp).graphicsLayer {
                    scaleX = .85f + .15f * burst.value; scaleY = scaleX
                })
            }
            Column(Modifier.fillMaxWidth().heightIn(min = (availableHeight - if (availableHeight < 650.dp) 260.dp else 400.dp).coerceAtLeast(280.dp)).padding(24.dp),
                horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.SpaceBetween) {
                Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    Text("Регистрация\nзавершена", color = colors.onSurface, style = MaterialTheme.typography.headlineLarge, fontWeight = FontWeight.Bold,
                        textAlign = androidx.compose.ui.text.style.TextAlign.Center, modifier = Modifier.semantics { heading() })
                    Text("Добро пожаловать, $name! Аккаунт @$username создан на $server.", style = MaterialTheme.typography.bodyLarge,
                        color = colors.onSurfaceVariant, textAlign = androidx.compose.ui.text.style.TextAlign.Center)
                }
                AuthPrimaryButton("Начать работу", onContinue, large = true)
            }
        }
    }
}
