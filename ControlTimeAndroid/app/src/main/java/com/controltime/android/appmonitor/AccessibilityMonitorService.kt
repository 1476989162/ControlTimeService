package com.controltime.android.appmonitor

import android.accessibilityservice.AccessibilityService
import android.content.Intent
import android.view.accessibility.AccessibilityEvent

/**
 * 无障碍服务 - 应用监控
 * 监听窗口变化，检测前台应用并识别违规应用
 */
class AccessibilityMonitorService : AccessibilityService() {

    companion object {
        // 单例引用
        var instance: AccessibilityMonitorService? = null
            private set

        // 违规应用包名黑名单
        val BLOCKED_PACKAGES = setOf(
            "com.tencent.mm",                   // 微信
            "com.tencent.game.rhythmmaster",    // 节奏大师
            "com.tencent.tmgp.pubgmhd",         // 和平精英
            "com.tencent.tmgp.sgame",           // 王者荣耀
            "com.tencent.tmgp.cod",             // 使命召唤手游
            "com.netease.hyxd",                 // 哈利波特
            "com.miHoYo.Yuanshen"               // 原神
        )

        // 违规应用窗口标题关键词
        val BLOCKED_WINDOW_TITLES = setOf(
            "微信小游戏", "小程序", "王者荣耀", "和平精英",
            "吃鸡", "原神", "小游戏", "抖音游戏"
        )

        // 回调接口
        var onViolationDetected: ((packageName: String, appName: String) -> Unit)? = null
    }

    override fun onServiceConnected() {
        super.onServiceConnected()
        instance = this
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        if (event == null) return

        when (event.eventType) {
            AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED -> {
                handleWindowStateChanged(event)
            }
        }
    }

    private fun handleWindowStateChanged(event: AccessibilityEvent) {
        val packageName = event.packageName?.toString() ?: return
        val text = event.text?.joinToString(" ") ?: ""

        // 检查包名是否在黑名单中
        if (BLOCKED_PACKAGES.contains(packageName)) {
            val appName = getAppName(packageName)
            onViolationDetected?.invoke(packageName, appName)
            return
        }

        // 检查窗口标题是否包含违规关键词
        for (keyword in BLOCKED_WINDOW_TITLES) {
            if (text.contains(keyword)) {
                val appName = getAppName(packageName)
                onViolationDetected?.invoke(packageName, appName)
                return
            }
        }
    }

    /**
     * 获取应用名称
     */
    private fun getAppName(packageName: String): String {
        return try {
            val pm = packageManager
            val ai = pm.getApplicationInfo(packageName, 0)
            pm.getApplicationLabel(ai).toString()
        } catch (e: Exception) {
            packageName
        }
    }

    override fun onInterrupt() {
        // 服务被中断
    }

    override fun onDestroy() {
        super.onDestroy()
        instance = null
    }
}
