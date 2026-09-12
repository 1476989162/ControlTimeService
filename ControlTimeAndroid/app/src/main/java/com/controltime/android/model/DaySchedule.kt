package com.controltime.android.model

import com.google.gson.annotations.SerializedName
import java.io.Serializable

/**
 * 每天的时间安排配置
 * 对应服务端的 DaySchedule 类
 */
data class DaySchedule(
    @SerializedName("day")
    var day: Int = 0,  // DayOfWeek: 0=Sunday, 1=Monday, ...

    @SerializedName("usageMinutes")
    var usageMinutes: Int = 30,

    @SerializedName("restMinutes")
    var restMinutes: Int = 30,

    @SerializedName("enabled")
    var enabled: Boolean = true,

    // 午间规则
    @SerializedName("lunchRestrictionEnabled")
    var lunchRestrictionEnabled: Boolean = true,

    @SerializedName("lunchMaxUsageMinutes")
    var lunchMaxUsageMinutes: Int = 60,

    @SerializedName("lunchStartTime")
    var lunchStartTime: String = "11:00",

    @SerializedName("lunchEndTime")
    var lunchEndTime: String = "14:00",

    // 晚间规则
    @SerializedName("eveningRestrictionEnabled")
    var eveningRestrictionEnabled: Boolean = true,

    @SerializedName("eveningMaxUsageMinutes")
    var eveningMaxUsageMinutes: Int = 30,

    @SerializedName("eveningStartTime")
    var eveningStartTime: String = "18:00",

    @SerializedName("eveningEndTime")
    var eveningEndTime: String = "20:30",

    // 夜间关机时间
    @SerializedName("nightShutdownTime")
    var nightShutdownTime: String = "20:30",

    // 早晨锁定
    @SerializedName("morningLockEnabled")
    var morningLockEnabled: Boolean = false,

    @SerializedName("morningUnlockTime")
    var morningUnlockTime: String = "08:00",

    // 应用权限
    @SerializedName("allowVideo")
    var allowVideo: Boolean = false,

    @SerializedName("allowWeChatMiniGames")
    var allowWeChatMiniGames: Boolean = true,

    @SerializedName("allowMaoxiang")
    var allowMaoxiang: Boolean = true,

    @SerializedName("allowDouyin")
    var allowDouyin: Boolean = true,

    @SerializedName("allowKuaishou")
    var allowKuaishou: Boolean = true,

    @SerializedName("allowXiaohongshu")
    var allowXiaohongshu: Boolean = true,

    @SerializedName("allowFanqieNovel")
    var allowFanqieNovel: Boolean = true,

    @SerializedName("allowTencentAppStore")
    var allowTencentAppStore: Boolean = true,

    @SerializedName("allowOtherGames")
    var allowOtherGames: Boolean = true,

    // 抖音游戏视频监控
    @SerializedName("blockDouyinGameVideos")
    var blockDouyinGameVideos: Boolean = true,

    @SerializedName("douyinGameVideoThresholdSeconds")
    var douyinGameVideoThresholdSeconds: Int = 10,

    @SerializedName("monitorDoubao")
    var monitorDoubao: Boolean = true
) : Serializable {

    companion object {
        @JvmStatic
        fun merge(existing: DaySchedule?, incoming: DaySchedule?): DaySchedule {
            val result = existing?.copy() ?: DaySchedule()
            if (incoming == null) return result

            if (incoming.usageMinutes > 0) result.usageMinutes = incoming.usageMinutes
            if (incoming.restMinutes > 0) result.restMinutes = incoming.restMinutes
            result.enabled = incoming.enabled

            result.lunchRestrictionEnabled = incoming.lunchRestrictionEnabled
            if (incoming.lunchMaxUsageMinutes >= 0) result.lunchMaxUsageMinutes = incoming.lunchMaxUsageMinutes
            if (incoming.lunchStartTime.isNotBlank()) result.lunchStartTime = incoming.lunchStartTime
            if (incoming.lunchEndTime.isNotBlank()) result.lunchEndTime = incoming.lunchEndTime
            result.eveningRestrictionEnabled = incoming.eveningRestrictionEnabled
            if (incoming.eveningMaxUsageMinutes >= 0) result.eveningMaxUsageMinutes = incoming.eveningMaxUsageMinutes
            if (incoming.eveningStartTime.isNotBlank()) result.eveningStartTime = incoming.eveningStartTime
            if (incoming.eveningEndTime.isNotBlank()) result.eveningEndTime = incoming.eveningEndTime
            if (incoming.nightShutdownTime.isNotBlank()) result.nightShutdownTime = incoming.nightShutdownTime
            result.morningLockEnabled = incoming.morningLockEnabled
            if (incoming.morningUnlockTime.isNotBlank()) result.morningUnlockTime = incoming.morningUnlockTime

            result.allowVideo = incoming.allowVideo
            result.allowWeChatMiniGames = incoming.allowWeChatMiniGames
            result.allowMaoxiang = incoming.allowMaoxiang
            result.allowDouyin = incoming.allowDouyin
            result.allowKuaishou = incoming.allowKuaishou
            result.allowXiaohongshu = incoming.allowXiaohongshu
            result.allowFanqieNovel = incoming.allowFanqieNovel
            result.allowTencentAppStore = incoming.allowTencentAppStore
            result.allowOtherGames = incoming.allowOtherGames
            result.blockDouyinGameVideos = incoming.blockDouyinGameVideos
            if (incoming.douyinGameVideoThresholdSeconds > 0) result.douyinGameVideoThresholdSeconds = incoming.douyinGameVideoThresholdSeconds
            result.monitorDoubao = incoming.monitorDoubao

            return result
        }

        @JvmStatic
        fun mergeConfig(
            existing: Map<String, DaySchedule>,
            incoming: Map<String, DaySchedule>?
        ): MutableMap<String, DaySchedule> {
            val result = existing.toMutableMap()
            if (incoming == null) return result

            for ((key, value) in incoming) {
                result[key] = merge(result[key], value)
            }
            return result
        }
    }

    fun copy(): DaySchedule {
        return DaySchedule(
            day = day,
            usageMinutes = usageMinutes,
            restMinutes = restMinutes,
            enabled = enabled,
            lunchRestrictionEnabled = lunchRestrictionEnabled,
            lunchMaxUsageMinutes = lunchMaxUsageMinutes,
            lunchStartTime = lunchStartTime,
            lunchEndTime = lunchEndTime,
            eveningRestrictionEnabled = eveningRestrictionEnabled,
            eveningMaxUsageMinutes = eveningMaxUsageMinutes,
            eveningStartTime = eveningStartTime,
            eveningEndTime = eveningEndTime,
            nightShutdownTime = nightShutdownTime,
            morningLockEnabled = morningLockEnabled,
            morningUnlockTime = morningUnlockTime,
            allowVideo = allowVideo,
            allowWeChatMiniGames = allowWeChatMiniGames,
            allowMaoxiang = allowMaoxiang,
            allowDouyin = allowDouyin,
            allowKuaishou = allowKuaishou,
            allowXiaohongshu = allowXiaohongshu,
            allowFanqieNovel = allowFanqieNovel,
            allowTencentAppStore = allowTencentAppStore,
            allowOtherGames = allowOtherGames,
            blockDouyinGameVideos = blockDouyinGameVideos,
            douyinGameVideoThresholdSeconds = douyinGameVideoThresholdSeconds,
            monitorDoubao = monitorDoubao
        )
    }

    fun toAppPolicy(): AppPolicy {
        return AppPolicy(
            allowVideo = allowVideo,
            allowWeChatMiniGames = allowWeChatMiniGames,
            allowMaoxiang = allowMaoxiang,
            allowDouyin = allowDouyin,
            allowKuaishou = allowKuaishou,
            allowXiaohongshu = allowXiaohongshu,
            allowFanqieNovel = allowFanqieNovel,
            allowTencentAppStore = allowTencentAppStore,
            allowOtherGames = allowOtherGames,
            blockDouyinGameVideos = blockDouyinGameVideos,
            douyinGameVideoThresholdSeconds = douyinGameVideoThresholdSeconds,
            monitorDoubao = monitorDoubao
        )
    }
}
