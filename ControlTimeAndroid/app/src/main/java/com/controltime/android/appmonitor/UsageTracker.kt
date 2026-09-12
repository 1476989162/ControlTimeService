package com.controltime.android.appmonitor

import android.app.usage.UsageStats
import android.app.usage.UsageStatsManager
import android.content.Context
import android.content.pm.ApplicationInfo
import android.content.pm.PackageManager
import java.text.SimpleDateFormat
import java.util.*

/**
 * 应用使用追踪器
 * 使用 UsageStatsManager 查询应用使用时长
 */
class UsageTracker(private val context: Context) {

    private val usageStatsManager: UsageStatsManager? by lazy {
        context.getSystemService(Context.USAGE_STATS_SERVICE) as? UsageStatsManager
    }

    private val packageManager: PackageManager by lazy {
        context.packageManager
    }

    private val dateFormat = SimpleDateFormat("yyyy-MM-dd", Locale.getDefault())

    /**
     * 获取今天的应用使用统计
     */
    fun getTodayUsageStats(): List<UsageStats> {
        if (usageStatsManager == null) return emptyList()

        val now = System.currentTimeMillis()
        val calendar = Calendar.getInstance().apply {
            set(Calendar.HOUR_OF_DAY, 0)
            set(Calendar.MINUTE, 0)
            set(Calendar.SECOND, 0)
            set(Calendar.MILLISECOND, 0)
        }
        val startTime = calendar.timeInMillis

        return usageStatsManager?.queryUsageStats(
            UsageStatsManager.INTERVAL_DAILY,
            startTime,
            now
        ) ?: emptyList()
    }

    /**
     * 获取今天特定应用的使用时长（秒）
     */
    fun getAppUsageSeconds(packageName: String): Long {
        val stats = getTodayUsageStats()
        val target = stats.firstOrNull { it.packageName == packageName }
        return target?.totalTimeInForeground?.div(1000) ?: 0
    }

    /**
     * 获取今天的使用总时长（秒），去除自身
     */
    fun getTotalUsageSeconds(excludeSelf: Boolean = true): Long {
        val stats = getTodayUsageStats()
        val myPackage = context.packageName
        var total = 0L
        for (stat in stats) {
            if (excludeSelf && stat.packageName == myPackage) continue
            total += stat.totalTimeInForeground / 1000
        }
        return total
    }

    /**
     * 获取已安装的用户应用列表（非系统应用）
     */
    fun getUserInstalledApps(): List<String> {
        val apps = packageManager.getInstalledApplications(PackageManager.GET_META_DATA)
        return apps.filter { app ->
            (app.flags and ApplicationInfo.FLAG_SYSTEM) == 0 ||
            app.packageName == "com.tencent.mm" // 微信也是系统应用但要监控
        }.map { it.packageName }
    }

    /**
     * 根据包名获取应用名称
     */
    fun getAppName(packageName: String): String {
        return try {
            val ai = packageManager.getApplicationInfo(packageName, 0)
            packageManager.getApplicationLabel(ai).toString()
        } catch (e: PackageManager.NameNotFoundException) {
            packageName
        }
    }

    /**
     * 根据名称获取包名
     */
    fun getPackageName(appName: String): String? {
        val apps = getTodayUsageStats()
        return stats.firstOrNull {
            getAppName(it.packageName).equals(appName, ignoreCase = true)
        }?.packageName
    }

    /**
     * 检查特定应用是否有过使用（今天）
     */
    fun isAppUsedToday(packageName: String): Boolean {
        val stats = getTodayUsageStats()
        return stats.any { it.packageName == packageName && it.totalTimeInForeground > 0 }
    }
}
