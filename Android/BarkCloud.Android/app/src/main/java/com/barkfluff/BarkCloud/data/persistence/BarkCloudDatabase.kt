package com.barkfluff.BarkCloud.data.persistence

import android.content.Context
import androidx.room.Database
import androidx.room.Room
import androidx.room.RoomDatabase
import androidx.room.TypeConverters
import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase
import com.barkfluff.BarkCloud.data.gallery.MediaCloudState
import com.barkfluff.BarkCloud.data.upload.UploadJob

@Database(
    entities = [UploadJob::class, MediaCloudState::class, FileCacheEntryEntity::class],
    version = 3,
    exportSchema = false,
)
@TypeConverters(BarkCloudConverters::class)
abstract class BarkCloudDatabase : RoomDatabase() {
    abstract fun uploadDao(): UploadDao
    abstract fun mediaCloudStateDao(): MediaCloudStateDao
    abstract fun fileCacheEntryDao(): FileCacheEntryDao

    companion object {
        @Volatile private var instance: BarkCloudDatabase? = null

        /** Upload 2.0: колонки сессии (uploadUrl/sourceUri из V1 остаются в таблице как неиспользуемые). */
        private val MIGRATION_1_2 = object : Migration(1, 2) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL("ALTER TABLE upload_jobs ADD COLUMN idempotencyKey TEXT")
                db.execSQL("ALTER TABLE upload_jobs ADD COLUMN sessionId TEXT")
                db.execSQL("ALTER TABLE upload_jobs ADD COLUMN partSize INTEGER NOT NULL DEFAULT 0")
            }
        }

        private val MIGRATION_2_3 = object : Migration(2, 3) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL(
                    "CREATE TABLE IF NOT EXISTS `file_cache_entries` (" +
                        "`key` TEXT NOT NULL, " +
                        "`scopeServer` TEXT NOT NULL, " +
                        "`accountId` TEXT NOT NULL, " +
                        "`fileId` TEXT NOT NULL, " +
                        "`variant` TEXT NOT NULL, " +
                        "`category` TEXT NOT NULL, " +
                        "`sizeBytes` INTEGER NOT NULL, " +
                        "`lastUsedAt` INTEGER NOT NULL, " +
                        "`fileName` TEXT NOT NULL, " +
                        "PRIMARY KEY(`key`))",
                )
                db.execSQL(
                    "CREATE INDEX IF NOT EXISTS `index_file_cache_entries_scopeServer_accountId_lastUsedAt` " +
                        "ON `file_cache_entries` (`scopeServer`, `accountId`, `lastUsedAt`)",
                )
                db.execSQL(
                    "CREATE INDEX IF NOT EXISTS `index_file_cache_entries_lastUsedAt_key` " +
                        "ON `file_cache_entries` (`lastUsedAt`, `key`)",
                )
            }
        }

        fun get(context: Context): BarkCloudDatabase = instance ?: synchronized(this) {
            instance ?: Room.databaseBuilder(
                context.applicationContext,
                BarkCloudDatabase::class.java,
                "barkcloud-local.db",
            )
                .addMigrations(MIGRATION_1_2, MIGRATION_2_3)
                .build()
                .also { instance = it }
        }
    }
}
