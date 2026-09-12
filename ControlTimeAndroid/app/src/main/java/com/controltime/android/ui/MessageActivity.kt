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
import com.controltime.android.model.ClientMessage
import com.controltime.android.network.ApiClient
import com.controltime.android.util.PreferencesManager
import kotlinx.coroutines.*
import java.text.SimpleDateFormat
import java.util.*

/**
 * 消息界面
 * 与管理端进行消息通信
 */
class MessageActivity : AppCompatActivity() {

    private lateinit var rvMessages: RecyclerView
    private lateinit var etMessage: EditText
    private lateinit var btnSend: Button
    private lateinit var tvStatus: TextView
    private lateinit var apiClient: ApiClient
    private lateinit var prefs: PreferencesManager
    private val serviceScope = CoroutineScope(Dispatchers.Main + SupervisorJob())

    private val messages = mutableListOf<ClientMessage>()
    private lateinit var adapter: MessageAdapter

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_message)

        prefs = PreferencesManager(this)
        val serverUrl = prefs.getServerUrl()
        apiClient = ApiClient(serverUrl)

        initViews()
        loadMessages()
    }

    private fun onDestroy() {
        super.onDestroy()
        serviceScope.cancel()
    }

    private fun initViews() {
        rvMessages = findViewById(R.id.rvMessages)
        etMessage = findViewById(R.id.etMessage)
        btnSend = findViewById(R.id.btnSend)
        tvStatus = findViewById(R.id.tvStatus)

        adapter = MessageAdapter(messages)
        rvMessages.layoutManager = LinearLayoutManager(this)
        rvMessages.adapter = adapter

        btnSend.setOnClickListener {
            sendMessage()
        }

        tvStatus.text = "已连接"
    }

    private fun loadMessages() {
        val clientId = prefs.getClientId() ?: return
        serviceScope.launch {
            val clientInfo = apiClient.getClientInfo(clientId)
            if (clientInfo != null) {
                val msgs = clientInfo.clientMessages
                if (msgs != null) {
                    messages.clear()
                    messages.addAll(msgs)
                    adapter.notifyDataSetChanged()
                }
            }
        }
    }

    private fun sendMessage() {
        val text = etMessage.text.toString().trim()
        if (text.isEmpty()) {
            Toast.makeText(this, "请输入消息内容", Toast.LENGTH_SHORT).show()
            return
        }

        val clientId = prefs.getClientId() ?: return
        serviceScope.launch {
            val result = apiClient.sendMessage(
                clientId,
                com.controltime.android.model.MessageRequest(text, "from_client")
            )
            if (result != null) {
                etMessage.setText("")
                messages.add(ClientMessage(text, "from_client", getCurrentTimestamp()))
                adapter.notifyItemInserted(messages.size - 1)
                rvMessages.scrollToPosition(messages.size - 1)
                Toast.makeText(this@MessageActivity, "已发送", Toast.LENGTH_SHORT).show()
            } else {
                Toast.makeText(this@MessageActivity, "发送失败", Toast.LENGTH_SHORT).show()
            }
        }
    }

    private fun getCurrentTimestamp(): String {
        val sdf = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.getDefault())
        return sdf.format(Date())
    }
}
