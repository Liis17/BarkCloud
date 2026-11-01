package com.barkfluff.BarkCloud.ui.components

import com.barkfluff.BarkCloud.data.cloud.CloudMediaKind
import com.barkfluff.BarkCloud.data.cloud.CloudMediaOrigin
import com.barkfluff.BarkCloud.data.cloud.MediaAsset
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class CloudMediaOriginTest {
    private fun asset(id: String, origin: CloudMediaOrigin, kind: CloudMediaKind = CloudMediaKind.PHOTO) = MediaAsset(
        id = id,
        fileName = "$id.jpg",
        fileSize = 1L,
        kind = kind,
        previews = emptyList(),
        createdAtMillis = 0L,
        origin = origin,
    )

    @Test
    fun sharedAssetsNeverEnableOwnOnlyActionsEvenWhenInfoIsOn() {
        val photo = cloudMediaActionVisibility(isVideo = false, infoEnabled = true, canDelete = true, origin = CloudMediaOrigin.SHARED)
        assertTrue(photo.share)
        assertTrue(photo.copy)
        assertTrue(photo.download)
        assertFalse(photo.info)
        assertFalse(photo.delete)
        assertFalse(photo.album)
        assertFalse(photo.favorite)

        val video = cloudMediaActionVisibility(isVideo = true, infoEnabled = true, canDelete = true, origin = CloudMediaOrigin.SHARED)
        assertTrue(video.share)
        assertFalse(video.copy)
        assertTrue(video.download)
        assertFalse(video.info)
        assertFalse(video.delete)
    }

    @Test
    fun ownAssetsKeepOwnOnlyActionsWhenAllowed() {
        val own = cloudMediaActionVisibility(isVideo = false, infoEnabled = true, canDelete = true, origin = CloudMediaOrigin.OWN)
        assertTrue(own.info)
        assertTrue(own.delete)
        assertTrue(own.album)
        assertTrue(own.favorite)
    }

    @Test
    fun sameSharedFileFromTwoSourcesAppearsOnce() {
        val items = listOf(
            asset("a", CloudMediaOrigin.SHARED),
            asset("a", CloudMediaOrigin.SHARED),
            asset("a", CloudMediaOrigin.OWN),
        )
        val distinct = distinctViewerAssets(items)
        assertEquals(listOf("SHARED:a", "OWN:a"), distinct.map { it.viewerId })
    }

    @Test
    fun pagerRequestsNextPageNearTheEndOnly() {
        assertFalse(shouldLoadMoreAtPagerEdge(settledIndex = 0, itemCount = 100))
        assertTrue(shouldLoadMoreAtPagerEdge(settledIndex = 97, itemCount = 100))
        assertFalse(shouldLoadMoreAtPagerEdge(settledIndex = 0, itemCount = 0))
    }
}
