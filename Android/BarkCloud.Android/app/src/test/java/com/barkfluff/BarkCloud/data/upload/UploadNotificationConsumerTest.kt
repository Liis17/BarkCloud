package com.barkfluff.BarkCloud.data.upload

import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Test

class UploadNotificationConsumerTest {
    @Test
    fun closingNotificationChannelLetsConsumerFinish() = runTest {
        val channel = Channel<Unit>(Channel.CONFLATED)
        var updates = 0
        val consumer = launch { consumeUploadNotifications(channel) { updates++ } }

        channel.send(Unit)
        channel.close()
        consumer.join()

        assertEquals(1, updates)
    }
}
