package com.barkfluff.BarkCloud

import androidx.activity.ComponentActivity
import androidx.compose.foundation.layout.Column
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.barkfluff.BarkCloud.ui.settings.ProfileAccountActions
import com.barkfluff.BarkCloud.ui.theme.BarkCloudTheme
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class ProfileAccountActionsTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()

    private fun showActions(onSignOut: () -> Unit = {}, onDelete: () -> Unit = {}) {
        compose.setContent {
            BarkCloudTheme {
                Column { ProfileAccountActions(true, false, onSignOut, onDelete) }
            }
        }
    }

    @Test fun signOutRequiresOneConfirmationAndCanBeCancelled() {
        var calls = 0
        showActions(onSignOut = { calls++ })
        compose.onNodeWithText("Выйти").performClick()
        compose.runOnIdle { assertEquals(0, calls) }
        compose.onNodeWithText("Отмена").performClick()
        compose.runOnIdle { assertEquals(0, calls) }
        compose.onNodeWithText("Выйти").performClick()
        compose.onAllNodesWithText("Выйти").onLast().performClick()
        compose.runOnIdle { assertEquals(1, calls) }
        compose.onNodeWithText("Выйти из аккаунта?").assertDoesNotExist()
    }

    @Test fun deletionRunsOnlyAfterThirdConfirmation() {
        var calls = 0
        showActions(onDelete = { calls++ })
        compose.onNodeWithText("Удалить аккаунт").performClick()
        repeat(2) {
            compose.onNodeWithText("Удаление аккаунта").assertIsDisplayed()
            compose.runOnIdle { assertEquals(0, calls) }
            compose.onNodeWithText("Продолжить").performClick()
        }
        compose.onNodeWithText("Удаление аккаунта").assertIsDisplayed()
        compose.runOnIdle { assertEquals(0, calls) }
        compose.onNodeWithText("Удалить безвозвратно").performClick()
        compose.runOnIdle { assertEquals(1, calls) }
    }

    @Test fun cancellationAtEveryDeletionStepRestartsFromFirstWarning() {
        var calls = 0
        showActions(onDelete = { calls++ })
        for (cancelAt in 1..3) {
            compose.onNodeWithText("Удалить аккаунт").performClick()
            compose.onNodeWithText("Удаление аккаунта").assertIsDisplayed()
            repeat(cancelAt - 1) { compose.onNodeWithText("Продолжить").performClick() }
            compose.onNodeWithText("Отмена").performClick()
            compose.onNodeWithText("Удаление аккаунта").assertDoesNotExist()
            compose.runOnIdle { assertEquals(0, calls) }
        }
    }
}
