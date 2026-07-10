package com.barkfluff.BarkCloud.data.persistence

import androidx.room.TypeConverter
import com.barkfluff.BarkCloud.data.gallery.MediaCloudStatus
import com.barkfluff.BarkCloud.data.upload.UploadDestination
import com.barkfluff.BarkCloud.data.upload.UploadPhase
import com.barkfluff.BarkCloud.data.upload.UploadSource

class BarkCloudConverters {
    @TypeConverter fun uploadSource(value: String): UploadSource = UploadSource.valueOf(value)
    @TypeConverter fun uploadSource(value: UploadSource): String = value.name
    @TypeConverter fun uploadDestination(value: String): UploadDestination = UploadDestination.valueOf(value)
    @TypeConverter fun uploadDestination(value: UploadDestination): String = value.name
    @TypeConverter fun uploadPhase(value: String): UploadPhase = UploadPhase.valueOf(value)
    @TypeConverter fun uploadPhase(value: UploadPhase): String = value.name
    @TypeConverter fun mediaCloudStatus(value: String): MediaCloudStatus = MediaCloudStatus.valueOf(value)
    @TypeConverter fun mediaCloudStatus(value: MediaCloudStatus): String = value.name
}
