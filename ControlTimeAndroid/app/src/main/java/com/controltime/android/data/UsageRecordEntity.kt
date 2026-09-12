package com.controltime.android.data

import androidx.room.Entity
import androidx.room.PrimaryKey

/**
 * 应用使用记录
 */
@Entity(tableName = "usage_record")
data class UsageRecordEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    val packageName: String,
    val appName: String = "",
    val usageSeconds: Long = 0,
    val date: String, // YYYY-MM-DD
    val timestamp: Long = 0,
    val isBlocked: Boolean = false
)
