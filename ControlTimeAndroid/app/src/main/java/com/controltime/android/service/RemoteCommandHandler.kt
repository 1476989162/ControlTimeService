package com.controltime.android.service

import android.content.Context
import android.content.Intent
import com.controltime.android.model.RemoteCommand
import com.controltime.android.timecontrol.LockScreenManager
import com.controltime.android.timecontrol.TimeController
import com.controltime.android.util.PreferencesManager

/**
 * 远程控制命令处理器
 * 解析和执行来自服务端的 RemoteCommand
 */
class RemoteCommandHandler(private val context: Context) {

    private val timeController = TimeController(context)
    private val prefs = PreferencesManager(context)

    /**
     * 处理远程命令
     */
    fun handleCommand(command: RemoteCommand) {
        when (command.command) {
            RemoteCommand.CMD_LOCK -> handleLock(command)
            RemoteCommand.CMD_UNLOCK -> handleUnlock(command)
            RemoteCommand.CMD_UPDATE_CONFIG -> handleUpdateConfig(command)
            RemoteCommand.CMD_UPDATE_APP_POLICY -> handleUpdateAppPolicy(command)
            RemoteCommand.CMD_PAUSE_TIMING -> handlePauseTiming(command)
            RemoteCommand.CMD_SHUTDOWN -> handleShutdown(command)
            RemoteCommand.CMD_SHOW_MESSAGE -> handleShowMessage(command)
            RemoteCommand.CMD_UPDATE -> handleUpdate(command)
        }
    }

    /**
     * 处理锁定命令
     */
    private fun handleLock(command: RemoteCommand) {
        val minutes = getMinutesFromParameters(command.parameters, 30)
        timeController.lock(minutes)
        LockScreenManager.startLockScreenActivity(
            context,
            reason = "远程锁定",
            remainingTime = "${minutes}分钟"
        )
        updateServiceNotification("屏幕已锁定", "锁定 ${minutes}分钟")
    }

    /**
     * 处理解锁命令
     */
    private fun handleUnlock(command: RemoteCommand) {
        val minutes = getMinutesFromParameters(command.parameters, 30)
        timeController.unlock(minutes)
        updateServiceNotification(
            "正常运行",
            "剩余时间: ${formatTime(timeController.remainingSeconds.toInt())}"
        )
    }

    /**
     * 处理配置更新
     */
    private fun handleUpdateConfig(command: RemoteCommand) {
        @Suppress("UNCHECKED_CAST")
        val configMap = command.parameters?.get("config") as? Map<String, com.controltime.android.model.DaySchedule>
        if (configMap != null) {
            // 更新本地配置
            timeController.updateFromRemoteConfig(configMap)
            updateServiceNotification("配置已更新", "接收到新的时间配置")
        }
    }

    /**
     * 处理应用权限更新
     */
    private fun handleUpdateAppPolicy(command: RemoteCommand) {
        TODO("实现应用权限更新逻辑")
    }

    /**
     * 处理暂停计时
     */
    private fun handlePauseTiming(command: RemoteCommand) {
        timeController.pauseTimer()
        updateServiceNotification("计时已暂停", "")
    }

    /**
     * 处理关机命令（停止服务）
     */
    private fun handleShutdown(command: RemoteCommand) {
        timeController.pauseTimer()
        val serviceIntent = Intent(context, ControlTimeService::class.java)
        serviceIntent.action = ControlTimeService.ACTION_STOP
        context.startService(serviceIntent)
    }

    /**
     * 处理显示消息命令
     */
    private fun handleShowMessage(command: RemoteCommand) {
        val text = command.parameters?.get("text") as? String ?: ""
        if (text.isNotBlank()) {
            updateServiceNotification("消息", text)
        }
    }

    /**
     * 处理应用更新命令
     */
    private fun handleUpdate(command: RemoteCommand) {
        val packageUrl = command.parameters?.get("package_url") as? String
        val version = command.parameters?.get("version") as? String
        if (!packageUrl.isNullOrBlank()) {
            updateServiceNotification("更新", "正在下载更新...")
            // TODO: 触发实际下载逻辑
        }
    }

    /**
     * 从参数中提取分钟数
     */
    private fun getMinutesFromParameters(parameters: Map<String, Any>?, defaultMinutes: Int): Int {
        val value = parameters?.get("minutes") ?: return defaultMinutes
        return when (value) {
            is Number -> value.toInt()
            is String -> value.toIntOrNull() ?: defaultMinutes
            else -> defaultMinutes
        }
    }

    /**
     * 更新服务通知
     */
    private fun updateServiceNotification(title: String, content: String) {
        val notificationHelper = NotificationHelper(context)
        notificationHelper.updateServiceNotification(title, content)
    }

    /**
     * 格式化时间
     */
    private fun formatTime(seconds: Int): String {
        val h = Math.abs(seconds) / 3600
        val m = (Math.abs(seconds) % 3600) / 60
        val s = Math.abs(seconds) % 60
        return String.format("%02d:%02d:%02d", h, m, s)
    }
}
