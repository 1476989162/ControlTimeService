package com.controltime.android.timecontrol

import java.util.Calendar
import java.util.TimeZone

/**
 * 时间表检查结果
 */
enum class ScheduleResult {
    USING,       // 正常使用
    RESTING,     // 休息中
    LIMITED,     // 限制使用（午间/晚间）
    SHUTDOWN,    // 夜间关机/锁定
    PAUSED       // 暂停计时
}

/**
 * 日程检查器
 * 根据当前时间和配置判断所处的时段状态
 */
class ScheduleChecker {

    /**
     * 检查当前时间表状态
     * @param usageMinutes 可用分钟数
     * @param restMinutes 休息分钟数
     * @param enabled 是否启用
     * @param nightShutdownTime 夜间关机时间 HH:mm
     * @param lunchRestrictionEnabled 午间限制启用
     * @param lunchStartTime 午间开始 HH:mm
     * @param lunchEndTime 午间结束 HH:mm
     * @param eveningRestrictionEnabled 晚间限制启用
     * @param eveningStartTime 晚间开始 HH:mm
     * @param eveningEndTime 晚间结束 HH:mm
     * @param morningLockEnabled 早晨锁定启用
     * @param morningUnlockTime 早晨解锁时间 HH:mm
     * @return ScheduleResult
     */
    fun checkCurrentSchedule(
        usageMinutes: Int = 30,
        restMinutes: Int = 30,
        enabled: Boolean = true,
        nightShutdownTime: String = "20:30",
        lunchRestrictionEnabled: Boolean = false,
        lunchStartTime: String = "11:00",
        lunchEndTime: String = "14:00",
        eveningRestrictionEnabled: Boolean = false,
        eveningStartTime: String = "18:00",
        eveningEndTime: String = "20:30",
        morningLockEnabled: Boolean = false,
        morningUnlockTime: String = "08:00"
    ): ScheduleResult {
        val now = Calendar.getInstance()
        val currentTime = getTimeFromCalendar(now)

        // 早晨锁定
        if (morningLockEnabled) {
            val unlockTime = parseTime(morningUnlockTime)
            if (currentTime < unlockTime) {
                return ScheduleResult.SHUTDOWN
            }
        }

        // 夜间关机
        val shutTime = parseTime(nightShutdownTime)
        if (currentTime >= shutTime) {
            return ScheduleResult.SHUTDOWN
        }

        // 午间限制
        if (lunchRestrictionEnabled) {
            val lunchStart = parseTime(lunchStartTime)
            val lunchEnd = parseTime(lunchEndTime)
            if (currentTime in lunchStart until lunchEnd) {
                return ScheduleResult.LIMITED
            }
        }

        // 晚间限制
        if (eveningRestrictionEnabled) {
            val eveningStart = parseTime(eveningStartTime)
            val eveningEnd = parseTime(eveningEndTime)
            if (currentTime in eveningStart until eveningEnd) {
                return ScheduleResult.LIMITED
            }
        }

        // 检查是否启用
        if (!enabled) {
            return ScheduleResult.SHUTDOWN
        }

        return ScheduleResult.USING
    }

    /**
     * 解析 HH:mm 格式的时间为从0点开始的分钟数
     */
    fun parseTime(timeStr: String): Int {
        return try {
            val parts = timeStr.split(":")
            parts[0].toInt() * 60 + parts[1].toInt()
        } catch (e: Exception) {
            0
        }
    }

    /**
     * 获取 Calendar 实例距离 0 点的分钟数
     */
    private fun getTimeFromCalendar(calendar: Calendar): Int {
        return calendar.get(Calendar.HOUR_OF_DAY) * 60 + calendar.get(Calendar.MINUTE)
    }

    /**
     * 计算从指定时间开始经过的分钟数
     */
    fun getMinutesSince(timeStr: String): Long {
        val targetMinutes = parseTime(timeStr)
        val now = Calendar.getInstance()
        val nowMinutes = getTimeFromCalendar(now)
        return (nowMinutes - targetMinutes).toLong()
    }

    companion object {
        /**
         * 判断当前是否处于暑假模式 (7月1日 ~ 8月31日)
         */
        fun isSummerMode(now: Calendar = Calendar.getInstance()): Boolean {
            val month = now.get(Calendar.MONTH) + 1 // Calendar.MONTH is 0-based
            return month in 7..8
        }
    }
}
