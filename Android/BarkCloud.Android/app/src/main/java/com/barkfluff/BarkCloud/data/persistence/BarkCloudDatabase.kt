package com.barkfluff.BarkCloud.data.persistence

import android.content.Context
import androidx.room.Database
import androidx.room.Room
import androidx.room.RoomDatabase
import androidx.room.TypeConverters
import com.barkfluff.BarkCloud.data.gallery.MediaCloudState
import com.barkfluff.BarkCloud.data.upload.UploadJob

@Database(
    entities = [UploadJob::class, MediaCloudState::class],
    version = 1,
    exportSchema = false,
)
@TypeConverters(BarkCloudConverters::class)
abstract class BarkCloudDatabase : RoomDatabase() {
    abstract fun uploadDao(): UploadDao
    abstract fun mediaCloudStateDao(): MediaCloudStateDao

    companion object {
        @Volatile private var instance: BarkCloudDatabase? = null

        fun get(context: Context): BarkCloudDatabase = instance ?: synchronized(this) {
            instance ?: Room.databaseBuilder(
                context.applicationContext,
                BarkCloudDatabase::class.java,
                "barkcloud-local.db",
            ).build().also { instance = it }
        }
    }
}
