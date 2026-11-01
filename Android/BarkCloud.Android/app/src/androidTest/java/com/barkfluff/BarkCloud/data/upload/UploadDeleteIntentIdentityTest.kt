package com.barkfluff.BarkCloud.data.upload

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit

/** Delete PendingIntent разных проходов различимы, а доставка старого возвращает его собственный токен. */
@RunWith(AndroidJUnit4::class)
class UploadDeleteIntentIdentityTest {
    private val context = InstrumentationRegistry.getInstrumentation().targetContext
    private val tally = UploadRunTally(done = 1, remaining = 1, failed = 0)

    @After
    fun tearDown() {
        UploadNotificationDismissal.onDismissReceived = null
    }

    @Test
    fun deleteIntentsOfDifferentRunsAreDistinct() {
        val runA = UploadNotificationDismissal.beginRun()
        val notificationA = UploadNotification.foreground(context, tally, runA)
        val runB = UploadNotificationDismissal.beginRun()
        val notificationB = UploadNotification.foreground(context, tally, runB)

        assertNotEquals(notificationA.deleteIntent, notificationB.deleteIntent)
    }

    @Test
    fun oldRunDeleteIntentDeliversItsOwnTokenAndCannotDismissNewRun() {
        val runA = UploadNotificationDismissal.beginRun()
        val oldNotification = UploadNotification.foreground(context, tally, runA)
        val runB = UploadNotificationDismissal.beginRun()
        UploadNotification.foreground(context, tally, runB)
        val delivered = LinkedBlockingQueue<String?>()
        UploadNotificationDismissal.onDismissReceived = { token -> delivered.add(token) }

        oldNotification.deleteIntent.send()

        assertEquals(runA, delivered.poll(DELIVERY_TIMEOUT_SECONDS, TimeUnit.SECONDS))
        assertFalse(UploadNotificationDismissal.isDismissed(runB))
    }

    @Test
    fun ownRunDeleteIntentDismissesItsRun() {
        val runA = UploadNotificationDismissal.beginRun()
        val notification = UploadNotification.foreground(context, tally, runA)
        val delivered = LinkedBlockingQueue<String?>()
        UploadNotificationDismissal.onDismissReceived = { token -> delivered.add(token) }

        notification.deleteIntent.send()

        assertEquals(runA, delivered.poll(DELIVERY_TIMEOUT_SECONDS, TimeUnit.SECONDS))
        assertTrue(UploadNotificationDismissal.isDismissed(runA))
    }

    private companion object {
        const val DELIVERY_TIMEOUT_SECONDS = 10L
    }
}
