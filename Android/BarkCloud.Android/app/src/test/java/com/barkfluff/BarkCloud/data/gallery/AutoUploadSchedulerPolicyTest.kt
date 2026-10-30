package com.barkfluff.BarkCloud.data.gallery

import androidx.work.NetworkType
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class AutoUploadSchedulerPolicyTest {
    @Test
    fun networkPolicyChangeReplacesOneTimeRequestWithUpdatedConstraint() {
        assertTrue(AutoUploadScheduler.policyChanged(AutoUploadNetworkPolicy.WIFI_ONLY, AutoUploadNetworkPolicy.ANY_NETWORK))
        assertEquals(NetworkType.UNMETERED, AutoUploadScheduler.networkType(AutoUploadNetworkPolicy.WIFI_ONLY))
        assertEquals(NetworkType.CONNECTED, AutoUploadScheduler.networkType(AutoUploadNetworkPolicy.ANY_NETWORK))
        assertFalse(AutoUploadScheduler.policyChanged(AutoUploadNetworkPolicy.ANY_NETWORK, AutoUploadNetworkPolicy.ANY_NETWORK))
        assertTrue(AutoUploadScheduler.policyChanged(null, AutoUploadNetworkPolicy.ANY_NETWORK))
    }
}
