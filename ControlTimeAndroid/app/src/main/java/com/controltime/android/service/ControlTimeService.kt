package com.controltime.android.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.PowerManager
import android.util.Log
import com.controltime.android.R
import com.controltime.android.network.ApiClient
import com.controltime.android.timecontrol.TimeController
import com.controltime.android.ui.MainActivity
import com.controltime.android.util.Constants
import com.controltime.android.util.NetworkUtils
import com.controltime.android.util.PreferencesManager
import kotlinx.coroutines.*
import java.util.*

/**
 * 时间控制前台服务
 * 保持应用在后台运行，处理计时和心跳
 */
class ControlTimeService : Service() {

    private val handler = Handler(Looper.getMainLooper())
    private val serviceScope = CoroutineScope(Dispatchers.Main + SupervisorJob())
    private var timeController: TimeController? = null
    private var apiClient: ApiClient? = null
    private var prefs: PreferencesManager? = null

    // 定时器
    private val timerRunnable = object : Runnable {
        override fun run() {
            if (timeController != null) {
                timeController!!.tick()
            }
            handler.postDelayed(this, 1000)
        }
    }

    // 命令轮询
    private val commandRunnable = object : Runnable {
        override fun run() {
            pollCommands()
            handler.postDelayed(this, Constants.POLL_INTERVAL_MS)
        }
    }

    override fun onCreate() {
        super.onCreate()
        prefs = PreferencesManager(this)
        timeController = TimeController(this)
        startForeground(Constants.NOTIFICATION_ID, createNotification())
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START -> handleStart()
            ACTION_STOP -> handleStop()
            ACTION_LOCK -> {
                val minutes = intent.getIntExtra("minutes", 30)
                handleLock(minutes)
            }
            ACTION_UNLOCK -> {
                val minutes = intent.getIntExtra("minutes", 30)
                handleUnlock(minutes)
            }
            ACTION_PAUSE -> handlePause()
            ACTION_RESUME -> handleResume()
            ACTION_SHUTDOWN -> handleShutdown()
            ACTION_MESSAGE -> {
                val text = intent.getStringExtra("text") ?: ""
                handleMessage(text)
            }
        }
        return START_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        handler.removeCallbacksAndMessages(null)
        serviceScope.cancel()
        super.onDestroy()
    }

    /**
     * 启动处理：注册、心跳、轮询
     */
    private fun handleStart() {
        val serverUrl = prefs?.getServerUrl() ?: return
        apiClient = ApiClient(serverUrl)

        // 启动计时器
        handler.post(timerRunnable)
        handler.post(commandRunnable)

        // 注册客户端
        serviceScope.launch {
            val clientId = prefs?.getClientId() ?: return@launch
            val deviceName = NetworkUtils.getDeviceName(this@ControlTimeService)
            apiClient?.register(
                com.controltime.android.model.ClientInfo(
                    id = clientId,
                    name = deviceName,
                    ipAddress = NetworkUtils.getLocalIpAddress(),
                    appVersion = BuildConfig.VERSION_NAME,
                    config = null,
                    appPolicy = null
                )
            )
        }

        // 启动心跳
        serviceScope.launch {
            while (isActive) {
                val clientId = prefs?.getClientId()
                if (clientId != null) {
                    val deviceName = NetworkUtils.getDeviceName(this@ControlTimeService)
                    apiClient?.heartbeat(clientId, deviceName)
                }
                delay(Constants.HEARTBEAT_INTERVAL_MS)
            }
        }
    }

    /**
     * 处理停止
     */
    private fun handleStop() {
        handler.removeCallbacks(timerRunnable)
        handler.removeCallbacks(commandRunnable)
        stopSelf()
    }

    /**
     * 轮询命令
     */
    private fun pollCommands() {
        val prefs = prefs ?: return
        if (!prefs.isServiceEnabled()) return

        val clientId = prefs.getClientId() ?: return
        val client = apiClient ?: return

        serviceScope.launch {
            try {
                val commands = client.getCommands(clientId)
                if (commands != null && commands.isNotEmpty()) {
                    for (command in commands) {
                        handleRemoteCommand(command)
                    }
                }
            } catch (e: Exception) {
                Log.e(TAG, "Command poll failed: ${e.message}")
            }
        }
    }

    /**
     * 处理远程命令
     */
    private fun handleRemoteCommand(command: com.controltime.android.model.RemoteCommand) {
        when (command.command) {
            "lock" -> {
                val minutes = (command.parameters?.get("minutes") as? Number)?.toInt() ?: 30
                handleLock(minutes)
            }
            "unlock" -> {
                val minutes = (command.parameters?.get("minutes") as? Number)?.toInt() ?: 30
                handleUnlock(minutes)
            }
            "update_config" -> {
                // 处理配置更新
                @Suppress("UNCHECKED_CAST")
                val configMap = command.parameters?.get("config") as? Map<String, com.controltime.android.model.DaySchedule>
                if (configMap != null) {
                    serviceScope.launch {
                        timeController?.updateFromRemoteConfig(configMap)
                    }
                }
            }
            "update_app_policy" -> {
                // 处理应用权限更新
            }
            "pause_timing" -> handlePause()
            "shutdown" -> handleShutdown()
            "show_message" -> {
                val text = command.parameters?.get("text") as? String ?: "收到新消息"
                handleMessage(text)
            }
            "update" -> {
                val packageUrl = command.parameters?.get("package_url") as? String
                if (!packageUrl.isNullOrBlank()) {
                    handleUpdate(packageUrl)
                }
            }
        }
    }

    /**
     * 锁定
     */
    private fun handleLock(minutes: Int) {
        timeController?.lock(minutes)
        updateNotification("屏幕已锁定", "锁定 ${minutes}分钟")
    }

    /**
     * 解锁
     */
    private fun handleUnlock(minutes: Int) {
        timeController?.unlock(minutes)
        updateNotification("正常运行", "剩余时间: ${timeController?.remainingSeconds?.let { formatTime(it.toInt()) }}")
    }

    /**
     * 暂停计时
     */
    private fun handlePause() {
        timeController?.pauseTimer()
        updateNotification("计时已暂停", "")
    }

    /**
     * 恢复计时
     */
    private fun handleResume() {
        timeController?.resumeTimer()
        updateNotification("正常运行", "剩余时间: ${timeController?.remainingSeconds?.let { formatTime(it.toInt()) }}")
    }

    /**
     * 关机/停止服务
     */
    private fun handleShutdown() {
        timeController?.pauseTimer()
        handleStop()
    }

    /**
     * 显示消息
     */
    private fun handleMessage(text: String) {
        updateNotification("消息", text)
    }

    /**
     * 处理更新
     */
    private fun handleUpdate(packageUrl: String) {
        // 下载并安装APK
        updateNotification("更新", "正在下载更新...")
    }

    /**
     * 创建通知
     */
    private fun createNotification(): Notification {
        val channelId = "control_time_service"
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                channelId,
                "时间控制服务",
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = "时间控制后台服务"
                setShowBadge(false)
            }
            val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            manager.createNotificationChannel(channel)
        }

        val pendingIntent = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        return Notification.Builder(this, channelId)
            .setContentTitle("时间控制")
            .setContentText("服务运行中")
            .setSmallIcon(R.drawable.ic_notification)
            .setContentIntent(pendingIntent)
            .setOngoing(true)
            .build()
    }

    /**
     * 更新通知
     */
    private fun updateNotification(title: String, text: String) {
        val channelId = "control_time_service"
        val pendingIntent = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        val notification = Notification.Builder(this, channelId)
            .setContentTitle(title)
            .setContentText(text)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentIntent(pendingIntent)
            .setOngoing(true)
            .build()

        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        manager.notify(Constants.NOTIFICATION_ID, notification)
    }

    /**
     * 格式化时间（秒 -> HH:mm:ss）
     */
    private fun formatTime(seconds: Int): String {
        val h = seconds / 3600
        val m = (seconds % 3600) / 60
        val s = seconds % 60
        return String.format("%02d:%02d:%02d", h, m, s)
    }

    companion object {
        const val TAG = "ControlTimeService"
        const val ACTION_START = "com.controltime.android.ACTION_START"
        const val ACTION_STOP = "com.controltime.android.ACTION_STOP"
        const val ACTION_LOCK = "com.controltime.android.ACTION_LOCK"
        const val ACTION_UNLOCK = "com.controltime.android.ACTION_UNLOCK"
        const val ACTION_PAUSE = "com.controltime.android.ACTION_PAUSE"
        const val ACTION_RESUME = "com.controltime.android.ACTION_RESUME"
        const val ACTION_SHUTDOWN = "com.controltime.android.ACTION_SHUTDOWN"
        const val ACTION_MESSAGE = "com.controltime.android.ACTION_MESSAGE"
    }
}
