package com.controltime.android.util

import android.content.Context
import android.content.SharedPreferences
import android.os.Build
import java.net.NetworkInterface
import java.util.*

/**
 * SharedPreferences 管理类
 */
class PreferencesManager(context: Context) {

    private val prefs: SharedPreferences = context.getSharedPreferences(
        "control_time_prefs",
        Context.MODE_PRIVATE
    )

    /**
     * 获取客户端 ID
     */
    fun getClientId(): String? {
        return prefs.getString("client_id", null)
    }

    /**
     * 设置客户端 ID
     */
    fun setClientId(clientId: String) {
        prefs.edit().putString("client_id", clientId).apply()
    }

    /**
     * 生成并保存客户端 ID（基于设备名）
     */
    fun generateAndSetClientId(context: Context): String {
        val existing = getClientId()
        if (existing != null) return existing

        val newId = generateClientId(context)
        setClientId(newId)
        return newId
    }

    /**
     * 生成客户端 ID
     */
    private fun generateClientId(context: Context): String {
        val deviceName = getDeviceName(context)
        val mac = getMacAddress()
        return "${deviceName}_${mac}"
    }

    /**
     * 获取设备名称
     */
    fun getDeviceName(context: Context): String {
        return prefs.getString("device_name", null)
            ?: Build.MODEL.let { model ->
                val name = if (model.isBlank()) "Android_${getMacAddress()}" else model
                prefs.edit().putString("device_name", name).apply()
                name
            }
    }

    /**
     * 设置设备名称
     */
    fun setDeviceName(name: String) {
        prefs.edit().putString("device_name", name).apply()
    }

    /**
     * 获取服务器 URL
     */
    fun getServerUrl(): String {
        return prefs.getString("server_url", "http://192.168.1.1:9528") ?: "http://192.168.1.1:9528"
    }

    /**
     * 设置服务器 URL
     */
    fun setServerUrl(url: String) {
        prefs.edit().putString("server_url", url).apply()
    }

    /**
     * 服务是否启用
     */
    fun isServiceEnabled(): Boolean {
        return prefs.getBoolean("service_enabled", false)
    }

    /**
     * 设置服务是否启用
     */
    fun setServiceEnabled(enabled: Boolean) {
        prefs.edit().putBoolean("service_enabled", enabled).apply()
    }

    /**
     * 获取解锁密码
     */
    fun getUnlockPassword(): String {
        return prefs.getString("unlock_password", "1234") ?: "1234"
    }

    /**
     * 设置解锁密码
     */
    fun setUnlockPassword(password: String) {
        prefs.edit().putString("unlock_password", password).apply()
    }

    /**
     * 获取上次使用日期（用于重置每日统计）
     */
    fun getLastUsageDate(): String? {
        return prefs.getString("last_usage_date", null)
    }

    /**
     * 设置上次使用日期
     */
    fun setLastUsageDate(date: String) {
        prefs.edit().putString("last_usage_date", date).apply()
    }

    /**
     * 移除 MAC 地址中的冒号
     */
    private fun getMacAddress(): String {
        try {
            val interfaces = NetworkInterface.getNetworkInterfaces()
            while (interfaces.hasMoreElements()) {
                val networkInterface = interfaces.nextElement()
                if (networkInterface.name.equals("wlan0", ignoreCase = true) ||
                    networkInterface.name.equals("eth0", ignoreCase = true)) {
                    val mac = networkInterface.hardwareAddress
                    if (mac != null) {
                        return mac.joinToString("") { String.format("%02X", it) }
                    }
                }
            }
        } catch (e: Exception) {
            // Ignore
        }
        return "unknown"
    }
}
