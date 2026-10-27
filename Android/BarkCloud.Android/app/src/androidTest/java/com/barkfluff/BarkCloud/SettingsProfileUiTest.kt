package com.barkfluff.BarkCloud

import android.net.Uri
import androidx.activity.ComponentActivity
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.unit.dp
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.barkfluff.BarkCloud.ui.main.MainScreen
import com.barkfluff.BarkCloud.ui.theme.BarkCloudTheme
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class SettingsProfileUiTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()

    @Test fun settingsHeaderIsCompactAndAvatarBelongsToProfile() {
        compose.setContent {
            BarkCloudTheme {
                MainScreen(deepLink = DeepLinkEvent(Uri.parse("barkcloud://settings"), sequence = 1), onSignOut = {})
            }
        }
        compose.onNodeWithText("Настройки").assertIsDisplayed()
        val title = compose.onNodeWithText("Настройки").getUnclippedBoundsInRoot()
        assertTrue("Settings title starts too low: ${title.top}", title.top < 100.dp)
        compose.onNodeWithText("Редактировать профиль").assertDoesNotExist()
        compose.onNodeWithContentDescription("Сменить фото").assertDoesNotExist()
        compose.onNodeWithText("Выйти").assertDoesNotExist()
        compose.onNodeWithText("Удалить аккаунт").assertDoesNotExist()
        compose.onNodeWithText("Профиль").performClick()
        compose.onNodeWithText("Имя").assertIsDisplayed()
        compose.onNodeWithContentDescription("Сменить фото").assertExists()
        compose.onNodeWithText("Удалить аккаунт").performScrollTo().assertIsDisplayed()
        compose.onNodeWithText("Выйти").assertIsDisplayed()
    }

}
