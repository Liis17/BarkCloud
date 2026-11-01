package com.barkfluff.BarkCloud.data.upload

import android.app.Notification
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class UploadForegroundRegistrationTest {
    private val fgs = Notification.FLAG_FOREGROUND_SERVICE
    private val ongoing = Notification.FLAG_ONGOING_EVENT

    @Test
    fun missingRegistrationIsNotRegistered() {
        assertFalse(isRegisteredForeground(emptyList(), runToken = "run-a"))
    }

    @Test
    fun otherNotificationIdIsNotRegistration() {
        val active = listOf(ActiveNotificationInfo(id = UploadNotification.FINISHED_ID, flags = fgs, runToken = "run-a"))

        assertFalse(isRegisteredForeground(active, runToken = "run-a"))
    }

    @Test
    fun wrongRunTokenIsNotRegistration() {
        val active = listOf(ActiveNotificationInfo(id = UploadNotification.ID, flags = fgs, runToken = "run-b"))

        assertFalse(isRegisteredForeground(active, runToken = "run-a"))
    }

    @Test
    fun missingRunTokenIsNotRegistration() {
        val active = listOf(ActiveNotificationInfo(id = UploadNotification.ID, flags = fgs, runToken = null))

        assertFalse(isRegisteredForeground(active, runToken = "run-a"))
    }

    @Test
    fun notForegroundServiceIsNotRegistration() {
        val active = listOf(ActiveNotificationInfo(id = UploadNotification.ID, flags = ongoing, runToken = "run-a"))

        assertFalse(isRegisteredForeground(active, runToken = "run-a"))
    }

    @Test
    fun matchingIdTokenAndForegroundFlagIsRegistration() {
        val active = listOf(
            ActiveNotificationInfo(id = UploadNotification.FINISHED_ID, flags = 0, runToken = null),
            ActiveNotificationInfo(id = UploadNotification.ID, flags = ongoing or fgs, runToken = "run-a"),
        )

        assertTrue(isRegisteredForeground(active, runToken = "run-a"))
    }
}
