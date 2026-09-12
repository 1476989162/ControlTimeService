package com.controltime.android.data

import android.content.Context
import androidx.room.Database
import androidx.room.Room
import androidx.room.RoomDatabase

/**
 * Room 数据库
 */
@Database(
    entities = [
        TimeConfigEntity::class,
        UsageRecordEntity::class,
        ClientInfoEntity::class
    ],
    version = 1,
    exportSchema = false
)
abstract class ControlTimeDatabase : RoomDatabase() {

    abstract fun timeConfigDao(): TimeConfigDao
    abstract fun usageRecordDao(): UsageRecordDao
    abstract fun clientInfoDao(): ClientInfoDao

    companion object {
        @Volatile
        private var INSTANCE: ControlTimeDatabase? = null

        fun getInstance(context: Context): ControlTimeDatabase {
            return INSTANCE ?: synchronized(this) {
                val instance = Room.databaseBuilder(
                    context.applicationContext,
                    ControlTimeDatabase::class.java,
                    "control_time.db"
                ).build()
                INSTANCE = instance
                instance
            }
        }
    }
}
