package com.barkfluff.BarkCloud.data.gallery

import android.content.Context
import android.content.SharedPreferences
import androidx.work.NetworkType
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.PeriodicWorkRequest
import androidx.work.WorkManager
import com.barkfluff.BarkCloud.data.upload.UniqueWorkEnqueuer
import com.barkfluff.BarkCloud.data.upload.restoreActiveUploadQueueAtStartup
import io.mockk.every
import io.mockk.mockk
import io.mockk.mockkObject
import io.mockk.unmockkObject
import io.mockk.verify
import kotlinx.coroutines.test.runTest
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

    @Test
    fun processRestartWithPersistedQueueAndSameWifiPolicyRestoresWorkWithUnmeteredConstraint() = runTest {
        val context = mockk<Context>()
        val preferences = mockk<SharedPreferences>()
        val editor = mockk<SharedPreferences.Editor>()
        val workManager = mockk<WorkManager>(relaxed = true)
        every { context.getSharedPreferences("barkcloud_auto_upload", Context.MODE_PRIVATE) } returns preferences
        every { preferences.getString("network_policy", null) } returns AutoUploadNetworkPolicy.WIFI_ONLY.name
        every { preferences.getString("last_applied_network_policy", null) } returns AutoUploadNetworkPolicy.WIFI_ONLY.name
        every { preferences.edit() } returns editor
        every { editor.putString(any(), any()) } returns editor
        every { editor.commit() } returns true

        val savedPolicy = AutoUploadSettings(context).policy
        assertEquals(AutoUploadNetworkPolicy.WIFI_ONLY, savedPolicy)
        val startupSteps = mutableListOf<String>()
        restoreActiveUploadQueueAtStartup(
            policy = savedPolicy,
            initializeQueue = { startupSteps += "initialize" },
            activeCount = { startupSteps += "active"; 2 },
            activeNonBackupCount = { 0 },
            enqueue = { startupSteps += "enqueue" },
        )
        assertEquals(listOf("initialize", "active", "enqueue"), startupSteps)

        mockkObject(WorkManager.Companion)
        mockkObject(UniqueWorkEnqueuer)
        every { WorkManager.getInstance(context) } returns workManager
        try {
            AutoUploadScheduler.apply(context, savedPolicy)

            verify {
                workManager.enqueueUniquePeriodicWork(
                    "barkcloud_auto_upload_periodic",
                    ExistingPeriodicWorkPolicy.UPDATE,
                    match<PeriodicWorkRequest> { it.workSpec.constraints.requiredNetworkType == NetworkType.UNMETERED },
                )
            }
            verify {
                UniqueWorkEnqueuer.enqueue(
                    workManager,
                    "barkcloud_auto_upload_once",
                    match { it.workSpec.constraints.requiredNetworkType == NetworkType.UNMETERED },
                )
            }
        } finally {
            unmockkObject(UniqueWorkEnqueuer)
            unmockkObject(WorkManager.Companion)
        }
    }
}
