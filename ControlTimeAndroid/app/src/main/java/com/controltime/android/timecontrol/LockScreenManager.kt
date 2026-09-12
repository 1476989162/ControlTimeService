package com.controltime.android.timecontrol

import android.app.Activity
import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.os.Build
import android.widget.Toast
import com.controltime.android.receiver.DeviceAdminReceiver

/**
 * 锁屏管理器
 * 使用 DevicePolicyManager 实现设备锁定
 */
class LockScreenManager(private val context: Context) {

    private val devicePolicyManager: DevicePolicyManager? by lazy {
        context.getSystemService(Context.DEVICE_POLICY_SERVICE) as? DevicePolicyManager
    }

    private val componentName: ComponentName by lazy {
        DeviceAdminReceiver.getComponentName(context)
    }

    /**
     * 检查是否已授予设备管理员权限
     */
    fun isDeviceAdminGranted(): Boolean {
        return devicePolicyManager?.isAdminActive(componentName) == true
    }

    /**
     * 立即锁定屏幕
     */
    fun lock() {
        if (isDeviceAdminGranted()) {
            devicePolicyManager?.lockNow()
            isLocked = true
        }
    }

    /**
     * 立即解锁（若已锁屏则不需要，本方法用于关闭自定义锁屏界面）
     */
    fun unlock() {
        isLocked = false
        // 如果在 Activity 中，关闭 Activity
        if (context is Activity) {
            context.finish()
        }
    }

    /**
     * 请求设备管理员权限
     */
    fun requestDeviceAdminPermission(activity: Activity, requestCode: Int = 100) {
        if (!isDeviceAdminGranted()) {
            val intent = Intent(DevicePolicyManager.ACTION_ADD_DEVICE_ADMIN).apply {
                putExtra(DevicePolicyManager.EXTRA_DEVICE_ADMIN, componentName)
                putExtra(
                    DevicePolicyManager.EXTRA_ADD_EXPLANATION,
                    "需要设备管理员权限来锁定屏幕，实现时间控制功能"
                )
            }
            activity.startActivityForResult(intent, requestCode)
        }
    }

    /**
     * 设置密码质量（确保锁屏安全）
     */
    fun setPasswordQuality() {
        if (isDeviceAdminGranted()) {
            devicePolicyManager?.setPasswordQuality(
                componentName,
                DevicePolicyManager.PASSWORD_QUALITY_SOMETHING
            )
        }
    }

    /**
     * 重置密码
     */
    fun resetPassword(password: String) {
        if (isDeviceAdminGranted()) {
            devicePolicyManager?.resetPassword(password, DevicePolicyManager.RESET_PASSWORD_REQUIRE_ENTRY)
        }
    }

    /**
     * 检查密码是否足够（防止简单密码）
     */
    fun isActivePasswordSufficient(): Boolean {
        return devicePolicyManager?.isActivePasswordSufficient == true
    }

    companion object {
        // 当前锁定状态（全局）
        var isLocked: Boolean = false
            set(value) {
                field = value
            }

        /**
         * 启动锁屏界面
         */
        fun startLockScreenActivity(context: Context, reason: String, remainingTime: String = "") {
            val intent = Intent(context, com.controltime.android.ui.LockScreenActivity::class.java).apply {
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                putExtra("reason", reason)
                putExtra("remainingTime", remainingTime)
            }
            context.startActivity(intent)
        }
    }
}
