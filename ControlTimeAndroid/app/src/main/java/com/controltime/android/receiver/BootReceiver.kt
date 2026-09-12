package com.controltime.android.receiver

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import com.controltime.android.service.ControlTimeService
import com.controltime.android.util.PreferencesManager

/**
 * 开机自启广播接收器
 */
class BootReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action == Intent.ACTION_BOOT_COMPLETED) {
            val prefs = PreferencesManager(context)
            if (prefs.isServiceEnabled()) {
                // 开机自启动服务
                val serviceIntent = Intent(context, ControlTimeService::class.java)
                serviceIntent.action = ControlTimeService.ACTION_START
                context.startForegroundService(serviceIntent)
            }
        }
    }
}
