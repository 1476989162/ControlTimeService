package com.controltime.android

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.os.Build

/**
 * Application 类
 */
class ControlTimeApplication : Application() {

    override fun onCreate() {
        super.onCreate()
        createNotificationChannels()
    }

    private fun createNotificationChannels() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager

            // 服务通知渠道
            val serviceChannel = NotificationChannel(
                "control_time_service",
                "时间控制服务",
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = "时间控制后台服务通知"
                setShowBadge(false)
            }
            manager.createNotificationChannel(serviceChannel)

            // 消息通知渠道
            val messageChannel = NotificationChannel(
                "control_time_message",
                "管理消息",
                NotificationManager.IMPORTANCE_DEFAULT
            ).apply {
                description = "来自管理端的消息"
            }
            manager.createNotificationChannel(messageChannel)
        }
    }
}
