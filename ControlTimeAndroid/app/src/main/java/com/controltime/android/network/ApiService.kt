package com.controltime.android.network

import com.controltime.android.model.ClientInfo
import com.controltime.android.model.ClientMessage
import com.controltime.android.model.RemoteCommand
import com.controltime.android.model.StatusUpdate
import com.controltime.android.model.MessageRequest
import com.google.gson.reflect.TypeToken

/**
 * HTTP API 服务接口
 * 封装所有与服务器的通信
 */
interface ApiService {

    /**
     * 客户端注册
     */
    suspend fun register(client: ClientInfo): String?

    /**
     * 心跳
     */
    suspend fun heartbeat(id: String, name: String): String?

    /**
     * 获取命令
     */
    suspend fun getCommands(clientId: String): List<RemoteCommand>?

    /**
     * 上报状态
     */
    suspend fun updateStatus(clientId: String, status: StatusUpdate): String?

    /**
     * 发送消息
     */
    suspend fun sendMessage(clientId: String, message: MessageRequest): String?

    /**
     * 获取客户端信息
     */
    suspend fun getClientInfo(clientId: String): ClientInfo?
}
