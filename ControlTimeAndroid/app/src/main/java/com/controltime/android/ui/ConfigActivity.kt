package com.controltime.android.ui

import android.os.Bundle
import android.widget.Button
import android.widget.EditText
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.controltime.android.R
import com.controltime.android.model.DaySchedule
import com.controltime.android.timecontrol.TimeController
import com.controltime.android.util.PreferencesManager
import java.util.*

/**
 * 配置界面
 * 显示和编辑每天的时间规则
 */
class ConfigActivity : AppCompatActivity() {

    private lateinit var timeController: TimeController
    private lateinit var prefs: PreferencesManager
    private lateinit var dayLabels: List<String>
    private lateinit var dayKeys: List<String>

    // UI elements
    private lateinit var tvSelectedDay: TextView
    private lateinit var etUsageMinutes: EditText
    private lateinit var etRestMinutes: EditText
    private lateinit var etLunchStart: EditText
    private lateinit var etLunchEnd: EditText
    private lateinit var etEveningStart: EditText
    private lateinit var etEveningEnd: EditText
    private lateinit var etNightShutdown: EditText
    private lateinit var btnSave: Button
    private lateinit var btnPrevDay: Button
    private lateinit var btnNextDay: Button

    private var selectedDayIndex = 0

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_config)

        timeController = TimeController(this)
        prefs = PreferencesManager(this)
        dayLabels = listOf("周日", "周一", "周二", "周三", "周四", "周五", "周六")
        dayKeys = listOf("Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday")

        initViews()
        loadConfig()
    }

    private fun initViews() {
        tvSelectedDay = findViewById(R.id.tvSelectedDay)
        etUsageMinutes = findViewById(R.id.etUsageMinutes)
        etRestMinutes = findViewById(R.id.etRestMinutes)
        etLunchStart = findViewById(R.id.etLunchStart)
        etLunchEnd = findViewById(R.id.etLunchEnd)
        etEveningStart = findViewById(R.id.etEveningStart)
        etEveningEnd = findViewById(R.id.etEveningEnd)
        etNightShutdown = findViewById(R.id.etNightShutdown)
        btnSave = findViewById(R.id.btnSave)
        btnPrevDay = findViewById(R.id.btnPrevDay)
        btnNextDay = findViewById(R.id.btnNextDay)

        btnPrevDay.setOnClickListener {
            if (selectedDayIndex > 0) {
                selectedDayIndex--
                loadConfig()
            }
        }

        btnNextDay.setOnClickListener {
            if (selectedDayIndex < dayLabels.size - 1) {
                selectedDayIndex++
                loadConfig()
            }
        }

        btnSave.setOnClickListener {
            saveConfig()
        }

        val calendar = Calendar.getInstance()
        selectedDayIndex = calendar.get(Calendar.DAY_OF_WEEK) - 1
    }

    private fun loadConfig() {
        tvSelectedDay.text = dayLabels[selectedDayIndex]
        // TODO: 从数据库加载指定日期的配置
        val schedule = DaySchedule()
        etUsageMinutes.setText(schedule.usageMinutes.toString())
        etRestMinutes.setText(schedule.restMinutes.toString())
        etLunchStart.setText(schedule.lunchStartTime)
        etLunchEnd.setText(schedule.lunchEndTime)
        etEveningStart.setText(schedule.eveningStartTime)
        etEveningEnd.setText(schedule.eveningEndTime)
        etNightShutdown.setText(schedule.nightShutdownTime)
    }

    private fun saveConfig() {
        val dayKey = dayKeys[selectedDayIndex]
        val usageMinutes = etUsageMinutes.text.toString().toIntOrNull() ?: 30
        val restMinutes = etRestMinutes.text.toString().toIntOrNull() ?: 30
        val lunchStart = etLunchStart.text.toString()
        val lunchEnd = etLunchEnd.text.toString()
        val eveningStart = etEveningStart.text.toString()
        val eveningEnd = etEveningEnd.text.toString()
        val nightShutdown = etNightShutdown.text.toString()

        // TODO: 保存到本地数据库，并同步到服务器
        Toast.makeText(this, "已保存 ${dayLabels[selectedDayIndex]} 的配置", Toast.LENGTH_SHORT).show()
    }
}
