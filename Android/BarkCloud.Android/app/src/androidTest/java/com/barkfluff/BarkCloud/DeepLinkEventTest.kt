package com.barkfluff.BarkCloud

import android.net.Uri
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

class DeepLinkEventTest {
    @Test
    fun initialLinkIsNotReplayedWhenActivityIsRestored() {
        val uri = Uri.parse("barkcloud://settings")

        assertNull(initialDeepLinkEvent(uri, restored = true))
        assertEquals(uri, initialDeepLinkEvent(uri, restored = false)?.uri)
    }

    @Test
    fun repeatedSameLinkProducesDistinctEvents() {
        val uri = Uri.parse("barkcloud://settings")
        val first = initialDeepLinkEvent(uri, restored = false, sequence = 0)
        val second = nextDeepLinkEvent(uri, sequence = first!!.sequence)

        assertNotNull(second)
        assertEquals(uri, second!!.uri)
        assertNotEquals(first, second)
    }
}
