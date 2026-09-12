package com.controltime.android.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.Build
import androidx.core.app.NotificationCompat
import com.controltime.android.R
import com.controltime.android.ui.MainActivity
import com.controltime.android.util.Constants

/**
 * 通知管理器
 */
class NotificationHelper(private val context: Context) {

    companion object {
        const val CHANNEL_ID_SERVICE = "control_time_service"
        const val CHANNEL_ID_MESSAGE = "control_time_message"
    }

    init {
        createNotificationChannels()
    }

    /**
     * 创建通知渠道
     */
    private fun createNotificationChannels() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val manager = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            
            // 服务渠道
            val serviceChannel = NotificationChannel(
                CHANNEL_ID_SERVICE,
                "时间控制服务",
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = "时间控制后台服务通知"
                setShowBadge(false)
            }
            manager.createNotificationChannel(serviceChannel)

            // 消息渠道
            val messageChannel = NotificationChannel(
                CHANNEL_ID_MESSAGE,
                "管理消息",
                NotificationManager.IMPORTANCE_DEFAULT
            ).apply {
                description = "来自管理端的消息"
            }
            manager.createNotificationChannel(messageChannel)
        }
    }

    /**
     * 构建服务通知
     */
    fun buildServiceNotification(title: String, content: String): Notification {
        val pendingIntent = PendingIntent.getActivity(
            context,
            0,
            Intent(context, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        return NotificationCompat.Builder(context, CHANNEL_ID_SERVICE)
            .setContentTitle(title)
            .setContentText(content)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentIntent(pendingIntent)
            .setOngoing(true)
            .build()
    }

    /**
     * 发送消息通知
     */
    fun sendNotification(id: Int, title: String, content: String) {
        val pendingIntent = PendingIntent.getActivity(
            context,
            id,
            Intent(context, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        val notification = NotificationCompat.Builder(context, CHANNEL_ID_MESSAGE)
            .setContentTitle(title)
            .setContentText(content)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentIntent(pendingIntent)
            .setAutoCancel(true)
            .build()

        val manager = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        manager.notify(id, notification)
    }

    /**
     * 更新服务通知
     */
    fun updateServiceNotification(title: String, content: String) {
        val notification = buildServiceNotification(title, content)
        val manager = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        manager.notify(Constants.NOTIFICATION_ID, notification)
    }

    /**
     * 取消所有通知
     */
    fun cancelAll() {
        val manager = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        manager.cancelAll()
    }
}
