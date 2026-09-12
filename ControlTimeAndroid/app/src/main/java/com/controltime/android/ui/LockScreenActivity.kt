package com.controltime.android.ui

import android.app.Activity
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.View
import android.view.WindowManager
import android.widget.Button
import android.widget.EditText
import android.widget.TextView
import android.widget.Toast
import com.controltime.android.R
import com.controltime.android.timecontrol.LockScreenManager

/**
 * 锁屏界面Activity
 * 覆盖全屏显示，用户必须输入正确密码才能解锁
 */
class LockScreenActivity : Activity() {

    private val handler = Handler(Looper.getMainLooper())
    private lateinit var lockScreenManager: LockScreenManager

    private lateinit var tvReason: TextView
    private lateinit var tvRemainingTime: TextView
    private lateinit var etPassword: EditText
    private lateinit var btnUnlock: Button
    private lateinit var tvTip: TextView

    private var reason: String = "屏幕已锁定"
    private var remainingTime: String = ""

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_lock_screen)

        // 保持屏幕常亮，显示在锁屏上
        window.addFlags(
            WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON or
            WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED or
            WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON
        )

        lockScreenManager = LockScreenManager(this)

        initViews()
        parseIntent()
        updateDisplay()
    }

    private fun initViews() {
        tvReason = findViewById(R.id.tvLockReason)
        tvRemainingTime = findViewById(R.id.tvRemainingTime)
        etPassword = findViewById(R.id.etPassword)
        btnUnlock = findViewById(R.id.btnUnlock)
        tvTip = findViewById(R.id.tvTip)

        btnUnlock = findViewById(R.id.btnUnlock)
        btnUnlock.setOnClickListener {
            attemptUnlock()
        }
    }

    private fun parseIntent() {
        reason = intent.getStringExtra("reason") ?: "屏幕已锁定"
        remainingTime = intent.getStringExtra("remainingTime") ?: ""
    }

    private fun updateDisplay() {
        tvReason.text = reason
        tvRemainingTime.text = if (remainingTime.isNotEmpty()) {
            "剩余时间: $remainingTime"
        } else {
            ""
        }
        tvTip.text = "请输入管理密码解锁"
    }

    private fun attemptUnlock() {
        val inputPassword = etPassword.text.toString()
        if (inputPassword.isEmpty()) {
            Toast.makeText(this, "请输入密码", Toast.LENGTH_SHORT).show()
            return
        }

        // 密码验证（简单实现）
        // 实际使用时应从SharedPreferences获取预设密码
        val correctPassword = getUnlockPassword()
        if (inputPassword == correctPassword) {
            lockScreenManager.unlock()
            finish()
        } else {
            Toast.makeText(this, "密码错误", Toast.LENGTH_SHORT).show()
            etPassword.setText("")
        }
    }

    /**
     * 获取解锁密码
     */
    private fun getUnlockPassword(): String {
        // 从SharedPreferences读取密码，默认"1234"
        val prefs = getSharedPreferences("control_time_prefs", MODE_PRIVATE)
        return prefs.getString("unlock_password", "1234") ?: "1234"
    }

    override fun onBackPressed() {
        // 禁用返回键
    }

    override fun onDestroy() {
        super.onDestroy()
        handler.removeCallbacksAndMessages(null)
    }
}
