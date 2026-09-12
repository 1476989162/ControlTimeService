package com.controltime.android.timecontrol

import android.content.Context
import com.controltime.android.data.*
import com.controltime.android.model.DaySchedule
import java.text.SimpleDateFormat
import java.util.*

/**
 * 时间控制引擎主控制器
 * 管理使用计时、休息倒计时、配置同步等核心逻辑
 */
class TimeController(private val context: Context) {

    private val dao = ControlTimeDatabase.getInstance(context).timeConfigDao()
    private val scheduleChecker = ScheduleChecker()
    private val dateFormat = SimpleDateFormat("yyyy-MM-dd", Locale.getDefault())
    private val dayFormat = SimpleDateFormat("EEEE", Locale.getDefault()) // Monday, Tuesday, ...

    // 回调接口
    interface TimeControlCallback {
        fun onRemainingTimeChanged(remainingSeconds: Double)
        fun onScheduleResultChanged(result: ScheduleResult)
        fun onUsageUpdated(totalUsageSeconds: Double)
        fun onConfigUpdated()
    }

    var callback: TimeControlCallback? = null

    // 当前计时器状态
    var remainingSeconds: Double = 0.0
        private set
    var totalUsageSecondsToday: Double = 0.0
        private set
    var isTimerPaused: Boolean = false
        private set
    var currentScheduleResult: ScheduleResult = ScheduleResult.USING
        private set
    var isLocked: Boolean = false
        private set

    /**
     * 获取今天星期几的字符串表示
     */
    fun getTodayOfWeek(): String {
        return dayFormat.format(Date())
    }

    /**
     * 获取今天的配置
     */
    suspend fun getTodaySchedule(): DaySchedule? {
        val dayName = getTodayOfWeek()
        return dao.getByDayOfWeek(dayName)?.toDaySchedule()
    }

    /**
     * 从服务器配置更新本地数据库
     */
    suspend fun updateFromRemoteConfig(remoteConfig: Map<String, DaySchedule>) {
        for ((dayName, schedule) in remoteConfig) {
            val entity = schedule.toEntity(dayName)
            dao.insert(entity)
        }
        callback?.onConfigUpdated()
    }

    /**
     * 应用暑假覆盖规则
     */
    private fun applySummerOverride(schedule: DaySchedule) {
        schedule.usageMinutes = 30
        schedule.restMinutes = 60
        schedule.morningLockEnabled = true
        schedule.morningUnlockTime = "08:00"
        schedule.nightShutdownTime = "20:30"
        schedule.lunchRestrictionEnabled = false
        schedule.eveningRestrictionEnabled = false
    }

    /**
     * 检查当前时间和配置，更新状态
     */
    suspend fun checkSchedule(): ScheduleResult {
        var schedule = getTodaySchedule() ?: DaySchedule()

        // 应用暑假模式覆盖
        if (ScheduleChecker.isSummerMode()) {
            applySummerOverride(schedule)
        }

        currentScheduleResult = scheduleChecker.checkCurrentSchedule(
            usageMinutes = schedule.usageMinutes,
            restMinutes = schedule.restMinutes,
            enabled = schedule.enabled,
            nightShutdownTime = schedule.nightShutdownTime,
            lunchRestrictionEnabled = schedule.lunchRestrictionEnabled,
            lunchStartTime = schedule.lunchStartTime,
            lunchEndTime = schedule.lunchEndTime,
            eveningRestrictionEnabled = schedule.eveningRestrictionEnabled,
            eveningStartTime = schedule.eveningStartTime,
            eveningEndTime = schedule.eveningEndTime,
            morningLockEnabled = schedule.morningLockEnabled,
            morningUnlockTime = schedule.morningUnlockTime
        )

        callback?.onScheduleResultChanged(currentScheduleResult)
        return currentScheduleResult
    }

    /**
     * 递减剩余时间
     */
    fun tick() {
        if (isTimerPaused || isLocked) return

        if (remainingSeconds > 0) {
            remainingSeconds -= 1.0
            if (remainingSeconds < 0) remainingSeconds = 0.0
        }
        totalUsageSecondsToday += 1.0
        
        callback?.onRemainingTimeChanged(remainingSeconds)
        callback?.onUsageUpdated(totalUsageSecondsToday)
    }

    /**
     * 设置剩余时间
     */
    fun setRemainingTime(seconds: Double) {
        remainingSeconds = seconds
        callback?.onRemainingTimeChanged(remainingSeconds)
    }

    /**
     * 设置今日已用时间
     */
    fun setTodayUsage(seconds: Double) {
        totalUsageSecondsToday = seconds
        callback?.onUsageUpdated(totalUsageSecondsToday)
    }

    /**
     * 暂停计时
     */
    fun pauseTimer() {
        isTimerPaused = true
    }

    /**
     * 恢复计时
     */
    fun resumeTimer() {
        isTimerPaused = false
    }

    /**
     * 锁定
     */
    fun lock(minutes: Int = 30) {
        isLocked = true
        // 锁定期间暂停计时
        isTimerPaused = true
    }

    /**
     * 解锁
     */
    fun unlock(minutes: Int = 30) {
        isLocked = false
        isTimerPaused = false
        // 解锁后设置剩余时间
        remainingSeconds = minutes * 60.0
        callback?.onRemainingTimeChanged(remainingSeconds)
    }

    /**
     * 重置每日使用时间
     */
    fun resetDailyUsage() {
        totalUsageSecondsToday = 0.0
        callback?.onUsageUpdated(totalUsageSecondsToday)
    }

    /**
     * 将 Entity 转为 Model
     */
    private fun TimeConfigEntity.toDaySchedule(): DaySchedule {
        return DaySchedule(
            day = 0,
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

    /**
     * 将 Model 转为 Entity
     */
    private fun DaySchedule.toEntity(dayOfWeek: String): TimeConfigEntity {
        return TimeConfigEntity(
            dayOfWeek = dayOfWeek,
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
}
