package com.controltime.android.data

import androidx.room.Entity
import androidx.room.PrimaryKey

/**
 * 每天的时间配置
 */
@Entity(tableName = "time_config")
data class TimeConfigEntity(
    @PrimaryKey val dayOfWeek: String, // "Monday", "Tuesday", ...
    val usageMinutes: Int = 30,
    val restMinutes: Int = 30,
    val enabled: Boolean = true,
    
    // 午间规则
    val lunchRestrictionEnabled: Boolean = true,
    val lunchMaxUsageMinutes: Int = 60,
    val lunchStartTime: String = "11:00",
    val lunchEndTime: String = "14:00",
    
    // 晚间规则
    val eveningRestrictionEnabled: Boolean = true,
    val eveningMaxUsageMinutes: Int = 30,
    val eveningStartTime: String = "18:00",
    val eveningEndTime: String = "20:30",
    
    // 夜间关机时间
    val nightShutdownTime: String = "20:30",
    
    // 早晨锁定
    val morningLockEnabled: Boolean = false,
    val morningUnlockTime: String = "08:00",
    
    // 应用权限
    val allowVideo: Boolean = false,
    val allowWeChatMiniGames: Boolean = true,
    val allowMaoxiang: Boolean = true,
    val allowDouyin: Boolean = true,
    val allowKuaishou: Boolean = true,
    val allowXiaohongshu: Boolean = true,
    val allowFanqieNovel: Boolean = true,
    val allowTencentAppStore: Boolean = true,
    val allowOtherGames: Boolean = true,
    val blockDouyinGameVideos: Boolean = true,
    val douyinGameVideoThresholdSeconds: Int = 10,
    val monitorDoubao: Boolean = true
)
