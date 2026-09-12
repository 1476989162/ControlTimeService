package com.controltime.android.ui

import android.content.Intent
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.widget.Button
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import com.controltime.android.R
import com.controltime.android.service.ControlTimeService
import com.controltime.android.timecontrol.LockScreenManager
import com.controltime.android.timecontrol.ScheduleResult
import com.controltime.android.timecontrol.TimeController
import com.controltime.android.util.NetworkUtils
import com.controltime.android.util.PreferencesManager
import java.util.concurrent.Executors

/**
 * 主界面
 * 显示设备状态、剩余时间、今日使用情况
 */
class MainActivity : AppCompatActivity() {

    private lateinit var tvDeviceName: TextView
    private lateinit var tvRemainingTime: TextView
    private lateinit var tvUsageToday: TextView
    private lateinit var tvStatus: TextView
    private lateinit var btnPause: Button
    private lateinit var btnConfig: Button
    private lateinit var btnMessage: Button
    private lateinit var btnPermission: Button

    private lateinit var timeController: TimeController
    private lateinit var prefs: PreferencesManager
    private val handler = Handler(Looper.getMainLooper())

    private val refreshRunnable = object : Runnable {
        override fun run() {
            refreshDisplay()
            handler.postDelayed(this, 1000)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        prefs = PreferencesManager(this)
        timeController = TimeController(this)

        initViews()
        checkAndGenerateClientId()
        startService()
    }

    override fun onResume() {
        super.onResume()
        handler.post(refreshRunnable)
    }

    override fun onPause() {
        super.onPause()
        handler.removeCallbacks(refreshRunnable)
    }

    private fun initViews() {
        tvDeviceName = findViewById(R.id.tvDeviceName)
        tvRemainingTime = findViewById(R.id.tvRemainingTime)
        tvUsageToday = findViewById(R.id.tvUsageToday)
        tvStatus = findViewById(R.id.tvStatus)
        btnPause = findViewById(R.id.btnPause)
        btnConfig = findViewById(R.id.btnConfig)
        btnMessage = findViewById(R.id.btnMessage)
        btnPermission = findViewById(R.id.btnPermission)

        tvDeviceName.text = prefs.getDeviceName(this)

        btnPause.setOnClickListener {
            togglePause()
        }

        btnConfig.setOnClickListener {
            startActivity(Intent(this, ConfigActivity::class.java))
        }

        btnMessage.setOnClickListener {
            startActivity(Intent(this, MessageActivity::class.java))
        }

        btnPermission.setOnClickListener {
            startActivity(Intent(this, PermissionGuideActivity::class.java))
        }
    }

    private fun checkAndGenerateClientId() {
        if (prefs.getClientId() == null) {
            val clientId = prefs.generateAndSetClientId(this)
            prefs.setClientId(clientId)
        }
    }

    private fun startService() {
        prefs.setServiceEnabled(true)
        val serviceIntent = Intent(this, ControlTimeService::class.java)
        serviceIntent.action = ControlTimeService.ACTION_START
        startForegroundService(serviceIntent)
    }

    private fun togglePause() {
        if (timeController.isTimerPaused) {
            timeController.resumeTimer()
            btnPause.text = "暂停计时"
        } else {
            timeController.pauseTimer()
            btnPause.text = "继续计时"
        }
    }

    private fun refreshDisplay() {
        val remaining = timeController.remainingSeconds
        val usage = timeController.totalUsageSecondsToday
        val result = timeController.currentScheduleResult
        val isLocked = timeController.isLocked

        tvRemainingTime.text = formatTime(remaining.toInt())
        tvUsageToday.text = "${formatTime(usage.toInt())}"

        val statusText = when {
            isLocked -> "⛔ 已锁定"
            timeController.isTimerPaused -> "⏸ 已暂停"
            result == ScheduleResult.USING -> "✅ 正常使用"
            result == ScheduleResult.LIMITED -> "⚠️ 限制使用"
            result == ScheduleResult.SHUTDOWN -> "🌙 夜间模式"
            result == ScheduleResult.RESTING -> "☕ 休息中"
            else -> "未知"
        }
        tvStatus.text = statusText
    }

    private fun formatTime(seconds: Int): String {
        val h = Math.abs(seconds) / 3600
        val m = (Math.abs(seconds) % 3600) / 60
        val s = Math.abs(seconds) % 60
        return String.format("%02d:%02d:%02d", h, m, s)
    }
}
