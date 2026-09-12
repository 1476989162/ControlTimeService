package com.controltime.android.service

import android.content.Context
import android.content.Intent
import android.os.Build
import com.controltime.android.appmonitor.AccessibilityMonitorService
import com.controltime.android.timecontrol.LockScreenManager
import com.controltime.android.timecontrol.TimeController

/**
 * WorkManager 心跳任务（保留备用）
 * 实际心跳直接使用 Coroutine 在 ControlTimeService 中运行
 * 此 WorkManager Worker 可用于额外的心跳保活或定期检查
 */
class HeartbeatWorker(
    context: Context,
    params: androidx.work.WorkerParameters
) : androidx.work.CoroutineWorker(context, params) {

    override suspend fun doWork(): Result {
        try {
            // 如果服务未运行，重新启动
            val serviceIntent = Intent(applicationContext, ControlTimeService::class.java)
            serviceIntent.action = ControlTimeService.ACTION_START
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                applicationContext.startForegroundService(serviceIntent)
            } else {
                applicationContext.startService(serviceIntent)
            }

            // 重置每日使用时间
            val timeController = TimeController(applicationContext)
            val prefs = com.controltime.android.util.PreferencesManager(applicationContext)
            val currentDate = java.text.SimpleDateFormat("yyyy-MM-dd", java.util.Locale.getDefault()).format(java.util.Date())
            val lastDate = prefs.getLastUsageDate()

            if (lastDate != currentDate) {
                // 新的一天，重置使用时间
                timeController.resetDailyUsage()
                prefs.setLastUsageDate(currentDate)
            }

            return Result.success()
        } catch (e: Exception) {
            return Result.retry()
        }
    }
}
