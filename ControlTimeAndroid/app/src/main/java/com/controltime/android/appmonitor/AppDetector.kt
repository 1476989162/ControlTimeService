package com.controltime.android.appmonitor

import android.content.Context
import android.provider.Settings
import android.text.TextUtils
import com.controltime.android.appmonitor.AccessibilityMonitorService.Companion.onViolationDetected

/**
 * 应用检测器
 * 综合 UsageStatsManager + AccessibilityService 检测违规应用
 */
class AppDetector(private val context: Context) {

    private val usageTracker = UsageTracker(context)

    /**
     * 检查无障碍服务是否已开启
     */
    fun isAccessibilityServiceEnabled(): Boolean {
        val serviceId = "${context.packageName}/${AccessibilityMonitorService::class.java.name}"
        val enabledServices = Settings.Secure.getString(
            context.contentResolver,
            Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES
        ) ?: return false

        val splitter = TextUtils.SimpleStringSplitter(':')
        splitter.setString(enabledServices)
        while (splitter.hasNext()) {
            if (splitter.next().equals(serviceId, ignoreCase = true)) {
                return true
            }
        }
        return false
    }

    /**
     * 启动违规检测回调
     */
    fun setupViolationCallback(callback: (packageName: String, appName: String) -> Unit) {
        onViolationDetected = callback
    }

    /**
     * 移除违规检测回调
     */
    fun removeViolationCallback() {
        onViolationDetected = null
    }

    /**
     * 扫描今天已经使用过的黑名单应用
     */
    fun scanBlockedAppsUsedToday(): List<Pair<String, String>> {
        val result = mutableListOf<Pair<String, String>>()
        for (pkg in AccessibilityMonitorService.BLOCKED_PACKAGES) {
            if (usageTracker.isAppUsedToday(pkg)) {
                result.add(pkg to usageTracker.getAppName(pkg))
            }
        }
        return result
    }

    /**
     * 根据当日应用权限策略，获取当前可用的应用
     */
    fun getAllowedApps(
        allowWeChatMiniGames: Boolean = true,
        allowMaoxiang: Boolean = true,
        allowDouyin: Boolean = true,
        allowKuaishou: Boolean = true,
        allowFanqieNovel: Boolean = true,
        allowTencentAppStore: Boolean = true,
        allowOtherGames: Boolean = true,
        allowVideo: Boolean = false
    ): Set<String> {
        val allowed = mutableSetOf<String>()
        // 添加白名单逻辑，例如允许某些应用使用
        return allowed
    }

    /**
     * 检查特定应用是否允许使用
     */
    fun isAppAllowed(packageName: String, policy: com.controltime.android.model.AppPolicy): Boolean {
        // 检查逻辑：根据包名和策略判断
        val lowerPackage = packageName.lowercase()
        
        when {
            lowerPackage.contains("com.tencent.mm") && !policy.allowWeChatMiniGames -> return false
            lowerPackage.contains("com.douyin") && !policy.allowDouyin -> return false
            lowerPackage.contains("com.kuaishou") && !policy.allowKuaishou -> return false
            (lowerPackage.contains("com.xingin.xhs") || lowerPackage.contains("xiaohongshu") || lowerPackage.contains("com.rednote")) && !policy.allowXiaohongshu -> return false
            lowerPackage.contains("com.fanqie") && !policy.allowFanqieNovel -> return false
            // ... 其他规则
        }
        return true
    }

    /**
     * 格式化时间（秒转为 HH:mm:ss）
     */
    fun formatTime(seconds: Long): String {
        val hours = seconds / 3600
        val minutes = (seconds % 3600) / 60
        val secs = seconds % 60
        return String.format("%02d:%02d:%02d", hours, minutes, secs)
    }
}
