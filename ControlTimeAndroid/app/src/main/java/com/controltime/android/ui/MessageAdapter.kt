package com.controltime.android.ui

import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.LinearLayout
import android.widget.TextView
import androidx.recyclerview.widget.RecyclerView
import com.controltime.android.R
import com.controltime.android.model.ClientMessage

/**
 * 消息列表适配器
 */
class MessageAdapter(
    private val messages: List<ClientMessage>
) : RecyclerView.Adapter<MessageAdapter.MessageViewHolder>() {

    class MessageViewHolder(view: View) : RecyclerView.ViewHolder(view) {
        val tvText: TextView = view.findViewById(R.id.tvMessageText)
        val tvTime: TextView = view.findViewById(R.id.tvMessageTime)
        val layout: LinearLayout = view.findViewById(R.id.llMessageItem)
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): MessageViewHolder {
        val view = LayoutInflater.from(parent.context)
            .inflate(R.layout.item_message, parent, false)
        return MessageViewHolder(view)
    }

    override fun onBindViewHolder(holder: MessageViewHolder, position: Int) {
        val message = messages[position]
        holder.tvText.text = message.text
        holder.tvTime.text = message.timestamp

        if (message.direction == ClientMessage.DIR_FROM_CLIENT) {
            // 发送的消息 - 右对齐
            holder.layout.gravity = android.view.Gravity.END
        } else {
            // 接收的消息 - 左对齐
            holder.layout.gravity = android.view.Gravity.START
        }
    }

    override fun getItemCount(): Int = messages.size
}
