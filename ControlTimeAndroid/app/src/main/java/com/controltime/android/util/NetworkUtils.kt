package com.controltime.android.util

import android.content.Context
import android.os.Build
import java.net.Inet4Address
import java.net.NetworkInterface
import java.util.*

/**
 * 网络工具类
 */
object NetworkUtils {

    /**
     * 获取本机 IP 地址
     */
    fun getLocalIpAddress(): String {
        try {
            val interfaces = NetworkInterface.getNetworkInterfaces()
            while (interfaces.hasMoreElements()) {
                val networkInterface = interfaces.nextElement()
                if (networkInterface.isLoopback || !networkInterface.isUp) continue

                val addresses = networkInterface.inetAddresses
                while (addresses.hasMoreElements()) {
                    val address = addresses.nextElement()
                    if (!address.isLoopbackAddress && address is Inet4Address) {
                        return address.hostAddress ?: "127.0.0.1"
                    }
                }
            }
        } catch (e: Exception) {
            // Ignore
        }
        return "127.0.0.1"
    }

    /**
     * 获取 MAC 地址
     */
    fun getMacAddress(): String {
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

    /**
     * 获取设备名称
     */
    fun getDeviceName(context: Context): String {
        // 尝试获取蓝牙名称
        try {
            val bluetoothAdapter = android.bluetooth.BluetoothAdapter.getDefaultAdapter()
            if (bluetoothAdapter != null && bluetoothAdapter.isEnabled) {
                val name = bluetoothAdapter.name
                if (!name.isNullOrBlank()) return name
            }
        } catch (e: Exception) {
            // Ignore
        }

        // 使用 Build.MODEL
        return Build.MODEL.ifBlank { "Android_${getMacAddress()}" }
    }

    /**
     * 根据 IP 获取同网段的服务端 IP
     */
    fun guessServerIp(): String {
        val localIp = getLocalIpAddress()
        val parts = localIp.split(".")
        if (parts.size == 4) {
            // 假设服务端是网关 192.168.x.1
            return "${parts[0]}.${parts[1]}.${parts[2]}.1"
        }
        return "192.168.1.1"
    }
}
