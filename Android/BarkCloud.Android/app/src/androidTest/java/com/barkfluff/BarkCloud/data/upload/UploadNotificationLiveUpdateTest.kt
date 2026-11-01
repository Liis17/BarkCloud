package com.barkfluff.BarkCloud.data.upload

import android.app.Notification
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.barkfluff.BarkCloud.R
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Структура foreground-уведомления прохода на Android 16+. Системный флаг `FLAG_PROMOTED_ONGOING`
 * не проверяется. `hasPromotableCharacteristics()` проверяется только там, где платформа объявляет
 * `POST_PROMOTED_NOTIFICATIONS` (Android 16 QPR1+): на API 36.0 разрешения нет, и promotion недоступен.
 */
@RunWith(AndroidJUnit4::class)
class UploadNotificationLiveUpdateTest {
    private val context = InstrumentationRegistry.getInstrumentation().targetContext

    @Test
    fun foregroundNotificationHasLiveUpdateShape() {
        assumeTrue("Live Update доступен с Android 16", Build.VERSION.SDK_INT >= 36)

        val notification = UploadNotification.foreground(context, UploadRunTally(done = 2, remaining = 6, failed = 0))

        assertTrue(NotificationCompat.isRequestPromotedOngoing(notification))
        assertEquals("2/8", NotificationCompat.getShortCriticalText(notification))
        assertTrue(notification.flags and Notification.FLAG_ONGOING_EVENT != 0)
        assertFalse(notification.flags and Notification.FLAG_GROUP_SUMMARY != 0)
        assertFalse(notification.extras.getBoolean(Notification.EXTRA_COLORIZED))
        assertNull(notification.contentView)
        assertNull(notification.bigContentView)
        assertNotNull(notification.contentIntent)
        assertNotNull(notification.deleteIntent)
        assertEquals(
            context.getString(R.string.upload_notification_title),
            notification.extras.getCharSequence(Notification.EXTRA_TITLE).toString(),
        )
        assertEquals("2 из 8", notification.extras.getCharSequence(Notification.EXTRA_TEXT).toString())
    }

    @Test
    fun foregroundNotificationIsPromotableWhereSystemDefinesPromotion() {
        assumeTrue("Платформа не объявляет POST_PROMOTED_NOTIFICATIONS", platformDefinesPromotion())

        val notification = UploadNotification.foreground(context, UploadRunTally(done = 2, remaining = 6, failed = 0))

        assertTrue(notification.hasPromotableCharacteristics())
    }

    private fun platformDefinesPromotion(): Boolean = runCatching {
        context.packageManager.getPermissionInfo("android.permission.POST_PROMOTED_NOTIFICATIONS", 0)
    }.isSuccess
}
