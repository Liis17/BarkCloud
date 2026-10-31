package com.barkfluff.BarkCloud.ui.navigation

import android.content.Context
import android.os.SystemClock
import androidx.activity.ComponentActivity
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onAllNodesWithText
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.lifecycle.Lifecycle
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.barkfluff.BarkCloud.BarkCloudApplication
import com.barkfluff.BarkCloud.R
import com.barkfluff.BarkCloud.data.update.AndroidReleaseMetadata
import com.barkfluff.BarkCloud.ui.theme.BarkCloudTheme
import java.time.LocalDate
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeFalse
import org.junit.Assume.assumeTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class RootNavGraphUpdatePromptTest {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()
    private val app get() = compose.activity.application as BarkCloudApplication

    @Test
    fun startupPromptStaysBehindAppLockAfterBackgroundReturn() {
        assumeTrue("Requires a restored app session", app.globalParam.sessionActive.value)
        assumeFalse("Keep any existing device PIN configuration", app.appLockStore.isEnabled)

        val preferences = app.getSharedPreferences("barkcloud_android_updates", Context.MODE_PRIVATE)
        val promptDayKey = "prompted_day_v1_0_5"
        val keys = listOf(
            "cached_release",
            "last_attempt",
            "last_startup_attempt",
            "rate_limit_until",
            "metadata_unavailable_tag",
            promptDayKey,
        )
        val originalValues = keys.associateWith { preferences.all[it] }
        val promptTitle = "Доступна BarkCloud 1.0.5"

        try {
            val release = AndroidReleaseMetadata(
                tag = "v1.0.5",
                versionName = "1.0.5",
                versionCode = 1_000_005,
                apkFileName = "BarkCloud.Android-v1.0.5.apk",
                apkSize = 128,
                sha256 = "ab".repeat(32),
                notes = "Lifecycle regression test",
            )
            val now = System.currentTimeMillis()
            check(preferences.edit()
                .putString("cached_release", release.toJson())
                .putLong("last_attempt", now)
                .putLong("last_startup_attempt", now)
                .putLong("rate_limit_until", 0)
                .remove("metadata_unavailable_tag")
                .remove(promptDayKey)
                .commit())
            app.appLockStore.enable(TEST_PIN)

            compose.setContent { BarkCloudTheme { RootNavGraph() } }
            compose.waitUntil(10_000) {
                compose.onAllNodesWithText(promptTitle).fetchSemanticsNodes().isNotEmpty()
            }
            compose.waitUntil(3_000) {
                preferences.getString(promptDayKey, null) == LocalDate.now().toString()
            }

            val backgroundedAt = SystemClock.elapsedRealtime()
            compose.activityRule.scenario.moveToState(Lifecycle.State.CREATED)
            compose.waitUntil(35_000) {
                SystemClock.elapsedRealtime() - backgroundedAt >= BACKGROUND_LOCK_MILLIS
            }
            compose.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)

            compose.waitUntil(10_000) { app.appLockManager.shouldShowLock.value }
            compose.onNodeWithText(compose.activity.getString(R.string.applock_unlock_title)).assertIsDisplayed()
            assertTrue(compose.onAllNodesWithText(promptTitle).fetchSemanticsNodes().isEmpty())

            TEST_PIN.forEach { digit -> compose.onNodeWithText(digit.toString()).performClick() }
            compose.waitUntil(10_000) { !app.appLockManager.shouldShowLock.value }
            assertTrue(compose.onAllNodesWithText(promptTitle).fetchSemanticsNodes().isEmpty())
        } finally {
            app.appLockStore.disable()
            app.appLockManager.unlock()
            val editor = preferences.edit()
            originalValues.forEach { (key, value) ->
                when (value) {
                    is String -> editor.putString(key, value)
                    is Long -> editor.putLong(key, value)
                    null -> editor.remove(key)
                    else -> error("Unexpected value type for updater test preference $key")
                }
            }
            check(editor.commit())
            compose.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        }
    }

    private companion object {
        const val TEST_PIN = "123456"
        const val BACKGROUND_LOCK_MILLIS = 32_000L
    }
}
