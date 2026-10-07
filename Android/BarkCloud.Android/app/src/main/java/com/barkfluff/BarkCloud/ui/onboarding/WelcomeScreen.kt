package com.barkfluff.BarkCloud.ui.onboarding

import androidx.activity.compose.BackHandler
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.animation.core.spring
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowForward
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.*
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.barkfluff.BarkCloud.ui.auth.components.WelcomeArt
import com.barkfluff.BarkCloud.ui.auth.components.rememberAuthMotionAllowed
import kotlinx.coroutines.launch

@Composable
fun WelcomeScreen(onFinished: () -> Unit) {
    val pager = rememberPagerState(pageCount = { 3 })
    val scope = rememberCoroutineScope()
    val motion = rememberAuthMotionAllowed()
    val fontScale = LocalDensity.current.fontScale
    val titles = listOf("Ваше облако.\nВаши правила.", "Фото — сразу\nв облако.", "Делитесь и\nработайте офлайн.")
    val descriptions = listOf(
        "Синхронизируйте файлы и фото со своим сервером — без сторонних хранилищ.",
        "Включите автозагрузку снимков на ваш сервер. Освобождайте память телефона, сохраняя фотографии в облаке.",
        "Отправляйте ссылки на файлы и папки. Уже сохранённые на телефоне файлы доступны без интернета.",
    )
    BackHandler(pager.currentPage > 0) { scope.launch { pager.animateScrollToPage(pager.currentPage - 1) } }
    BoxWithConstraints(Modifier.fillMaxSize().background(MaterialTheme.colorScheme.surface).windowInsetsPadding(WindowInsets.safeDrawing)) {
        val availableHeight = maxHeight
        Column(Modifier.widthIn(max = 480.dp).fillMaxWidth().fillMaxHeight().align(Alignment.Center)) {
            WelcomeArt(pager.currentPage, motion, Modifier.height(
                if (availableHeight < 500.dp) (availableHeight * .22f).coerceIn(64.dp, 100.dp)
                else if (fontScale > 1.3f) (availableHeight * .25f).coerceIn(140.dp, 240.dp)
                else (availableHeight * .52f).coerceIn(220.dp, 440.dp)))
            HorizontalPager(pager, Modifier.weight(1f), beyondViewportPageCount = 0) { page ->
                Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(24.dp),
                    verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    Text(titles[page], color = MaterialTheme.colorScheme.onSurface, style = MaterialTheme.typography.displaySmall, fontWeight = FontWeight.Bold,
                        modifier = Modifier.semantics { heading() })
                    Text(descriptions[page], style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
            Column(Modifier.padding(horizontal = 24.dp, vertical = 16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Row(Modifier.fillMaxWidth().clearAndSetSemantics { contentDescription = "Экран ${pager.currentPage + 1} из 3" },
                    horizontalArrangement = Arrangement.Center, verticalAlignment = Alignment.CenterVertically) {
                    repeat(3) { page ->
                        val width by animateDpAsState(if (page == pager.currentPage) 20.dp else 8.dp, spring(.9f, 700f), label = "page indicator")
                        val color by animateColorAsState(if (page == pager.currentPage) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outlineVariant, label = "indicator color")
                        Box(Modifier.padding(horizontal = 4.dp).size(width, 8.dp).background(color, RoundedCornerShape(4.dp)))
                    }
                }
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
                    if (pager.currentPage > 0) TextButton(onClick = { scope.launch { pager.animateScrollToPage(pager.currentPage - 1) } },
                        modifier = Modifier.heightIn(min = 48.dp)) { Text("Назад") }
                    else Spacer(Modifier.width(48.dp))
                    Button(onClick = {
                        if (pager.currentPage == 2) onFinished() else scope.launch { pager.animateScrollToPage(pager.currentPage + 1) }
                    }, modifier = Modifier.heightIn(min = 56.dp), shape = RoundedCornerShape(28.dp)) {
                        Text(if (pager.currentPage == 2) "Сервер" else "Далее")
                        Spacer(Modifier.width(8.dp))
                        Icon(Icons.AutoMirrored.Outlined.ArrowForward, null, modifier = Modifier.size(20.dp))
                    }
                }
            }
        }
    }
}
