package com.controltime.android.ui

import android.app.AppOpsManager
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import android.widget.Button
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import com.controltime.android.R
import com.controltime.android.appmonitor.AppDetector
import com.controltime.android.timecontrol.LockScreenManager

/**
 * 权限引导界面
 * 逐步引导用户授权所需权限
 */
class PermissionGuideActivity : AppCompatActivity() {

    private lateinit var lockScreenManager: LockScreenManager
    private lateinit var appDetector: AppDetector

    private lateinit var tvDeviceAdminStatus: TextView
    private lateinit var tvAccessibilityStatus: TextView
    private lateinit var tvUsageStatsStatus: TextView
    private lateinit var tvBatteryStatus: TextView
    private lateinit var tvOverlayStatus: TextView

    private lateinit var btnDeviceAdmin: Button
    private lateinit var btnAccessibility: Button
    private lateinit var btnUsageStats: Button
    private lateinit var btnBattery: Button
    private lateinit var btnOverlay: Button
    private lateinit var btnCheckAll: Button

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_permission_guide)

        lockScreenManager = LockScreenManager(this)
        appDetector = AppDetector(this)

        initViews()
        updateStatus()
    }

    override fun onResume() {
        super.onResume()
        updateStatus()
    }

    private fun initViews() {
        tvDeviceAdminStatus = findViewById(R.id.tvDeviceAdminStatus)
        tvAccessibilityStatus = findViewById(R.id.tvAccessibilityStatus)
        tvUsageStatsStatus = findViewById(R.id.tvUsageStatsStatus)
        tvBatteryStatus = findViewById(R.id.tvBatteryStatus)
        tvOverlayStatus = findViewById(R.id.tvOverlayStatus)

        btnDeviceAdmin = findViewById(R.id.btnDeviceAdmin)
        btnAccessibility = findViewById(R.id.btnAccessibility)
        btnUsageStats = findViewById(R.id.btnUsageStats)
        btnBattery = findViewById(R.id.btnBattery)
        btnOverlay = findViewById(R.id.btnOverlay)
        btnCheckAll = findViewById(R.id.btnCheckAll)

        btnDeviceAdmin.setOnClickListener {
            lockScreenManager.requestDeviceAdminPermission(this)
        }

        btnAccessibility.setOnClickListener {
            val intent = Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)
            startActivity(intent)
        }

        btnUsageStats.setOnClickListener {
            val intent = Intent(Settings.ACTION_USAGE_ACCESS_SETTINGS)
            startActivity(intent)
        }

        btnBattery.setOnClickListener {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                val intent = Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)
                startActivity(intent)
            }
        }

        btnOverlay.setOnClickListener {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                val intent = Intent(
                    Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                    Uri.parse("package:$packageName")
                )
                startActivity(intent)
            }
        }

        btnCheckAll.setOnClickListener {
            updateStatus()
        }
    }

    private fun updateStatus() {
        val deviceAdminGranted = lockScreenManager.isDeviceAdminGranted()
        val accessibilityGranted = appDetector.isAccessibilityServiceEnabled()
        val usageStatsGranted = checkUsageStatsPermission()
        val batteryOptimized = checkBatteryOptimization()
        val overlayGranted = checkOverlayPermission()

        tvDeviceAdminStatus.text = if (deviceAdminGranted) "已授权" else "未授权"
        tvDeviceAdminStatus.setTextColor(if (deviceAdminGranted) 0xFF4CAF50.toInt() else 0xFFE91E63.toInt())

        tvAccessibilityStatus.text = if (accessibilityGranted) "已授权" else "未授权"
        tvAccessibilityStatus.setTextColor(if (accessibilityGranted) 0xFF4CAF50.toInt() else 0xFFE91E63.toInt())

        tvUsageStatsStatus.text = if (usageStatsGranted) "已授权" else "未授权"
        tvUsageStatsStatus.setTextColor(if (usageStatsGranted) 0xFF4CAF50.toInt() else 0xFFE91E63.toInt())

        tvBatteryStatus.text = if (batteryOptimized) "已关闭优化" else "未关闭"
        tvBatteryStatus.setTextColor(if (batteryOptimized) 0xFF4CAF50.toInt() else 0xFFE91E63.toInt())

        tvOverlayStatus.text = if (overlayGranted) "已授权" else "未授权"
        tvOverlayStatus.setTextColor(if (overlayGranted) 0xFF4CAF50.toInt() else 0xFFE91E63.toInt())
    }

    private fun checkUsageStatsPermission(): Boolean {
        return try {
            val appInfo = packageManager.getApplicationInfo(packageName, 0)
            val mode = getSystemService(AppOpsManager::class.java)
                ?.checkOpNoThrow(
                    AppOpsManager.OPSTR_GET_USAGE_STATS,
                    appInfo.uid,
                    packageName
                )
            mode == AppOpsManager.MODE_ALLOWED
        } catch (e: Exception) {
            false
        }
    }

    private fun checkBatteryOptimization(): Boolean {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.M) return true
        val pm = getSystemService(POWER_SERVICE) as PowerManager
        return pm.isIgnoringBatteryOptimizations(packageName)
    }

    private fun checkOverlayPermission(): Boolean {
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            Settings.canDrawOverlays(this)
        } else {
            true
        }
    }
}
