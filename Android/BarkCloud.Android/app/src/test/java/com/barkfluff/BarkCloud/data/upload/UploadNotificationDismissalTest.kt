package com.barkfluff.BarkCloud.data.upload

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class UploadNotificationDismissalTest {
    @Test
    fun dismissalCountsOnlyForItsOwnRun() {
        val first = UploadNotificationDismissal.beginRun()

        UploadNotificationDismissal.markDismissed("stale-run-token")
        assertFalse(UploadNotificationDismissal.isDismissed(first))

        UploadNotificationDismissal.markDismissed(first)
        assertTrue(UploadNotificationDismissal.isDismissed(first))
    }

    @Test
    fun newRunStartsUndismissedAndOldTokenStaysDismissed() {
        val first = UploadNotificationDismissal.beginRun()
        UploadNotificationDismissal.markDismissed(first)

        val second = UploadNotificationDismissal.beginRun()

        assertFalse(UploadNotificationDismissal.isDismissed(second))
        assertFalse(UploadNotificationDismissal.isDismissed(first))
    }

    @Test
    fun missingTokenIsIgnored() {
        val run = UploadNotificationDismissal.beginRun()

        UploadNotificationDismissal.markDismissed(null)

        assertFalse(UploadNotificationDismissal.isDismissed(run))
    }
}
