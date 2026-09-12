package com.controltime.android.receiver

import android.app.admin.DeviceAdminReceiver
import android.content.Context
import android.content.Intent
import android.widget.Toast

/**
 * 设备管理器广播接收器
 * 用于实现屏幕锁定功能
 */
class DeviceAdminReceiver : DeviceAdminReceiver() {

    override fun onEnabled(context: Context, intent: Intent) {
        super.onEnabled(context, intent)
        Toast.makeText(context, "设备管理器已激活", Toast.LENGTH_SHORT).show()
    }

    override fun onDisabled(context: Context, intent: Intent) {
        super.onDisabled(context, intent)
        Toast.makeText(context, "设备管理器已停用", Toast.LENGTH_SHORT).show()
    }

    override fun onDisableRequested(context: Context, intent: Intent): CharSequence? {
        return "停用设备管理器将导致时间控制功能失效，确定要继续吗？"
    }

    companion object {
        fun getComponentName(context: Context): android.content.ComponentName {
            return android.content.ComponentName(context, DeviceAdminReceiver::class.java)
        }
    }
}
