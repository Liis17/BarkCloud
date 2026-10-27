package com.barkfluff.BarkCloud.ui.gallery

import com.barkfluff.BarkCloud.data.cloud.MediaAsset
import com.barkfluff.BarkCloud.data.gallery.DeviceMedia

internal sealed interface PhotoTimelineItem {
    val key: String
    val dateMillis: Long

    data class Device(val media: DeviceMedia) : PhotoTimelineItem {
        override val key: String get() = "device:${media.mediaKey}"
        override val dateMillis: Long get() = media.dateTakenMillis
    }

    data class Cloud(val asset: MediaAsset) : PhotoTimelineItem {
        override val key: String get() = "cloud:${asset.id}"
        override val dateMillis: Long get() = asset.createdAtMillis
    }
}
