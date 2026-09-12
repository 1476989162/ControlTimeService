package com.controltime.android.model

import com.google.gson.annotations.SerializedName
import java.io.Serializable

/**
 * 客户端注册信息
 * 对应服务端 ClientInfo 类
 */
data class ClientInfo(
    @SerializedName("id")
    var id: String = "",

    @SerializedName("name")
    var name: String = "",

    @SerializedName("computerName")
    var computerName: String = "",

    @SerializedName("ip")
    var ipAddress: String = "",

    @SerializedName("appVersion")
    var appVersion: String = "",

    @SerializedName("config")
    var config: Map<String, DaySchedule>? = null,

    @SerializedName("appPolicy")
    var appPolicy: AppPolicy? = null,

    @SerializedName("remainingSeconds")
    var remainingSeconds: Double = 0.0,

    @SerializedName("totalUsageSecondsToday")
    var totalUsageSecondsToday: Double = 0.0,

    @SerializedName("clientMessages")
    var clientMessages: List<ClientMessage>? = null
) : Serializable

/**
 * 远程控制命令
 */
data class RemoteCommand(
    @SerializedName("command")
    var command: String = "",

    @SerializedName("parameters")
    var parameters: Map<String, Any>? = null,

    @SerializedName("createdAt")
    var createdAt: String = ""
) : Serializable {
    companion object {
        // 命令类型常量
        const val CMD_LOCK = "lock"
        const val CMD_UNLOCK = "unlock"
        const val CMD_UPDATE_CONFIG = "update_config"
        const val CMD_UPDATE_APP_POLICY = "update_app_policy"
        const val CMD_PAUSE_TIMING = "pause_timing"
        const val CMD_SHUTDOWN = "shutdown"
        const val CMD_SHOW_MESSAGE = "show_message"
        const val CMD_UPDATE = "update"
    }
}

/**
 * 客户端消息
 */
data class ClientMessage(
    @SerializedName("text")
    var text: String = "",

    @SerializedName("direction")
    var direction: String = "to_client",

    @SerializedName("timestamp")
    var timestamp: String = ""
) : Serializable {
    companion object {
        const val DIR_TO_CLIENT = "to_client"
        const val DIR_FROM_CLIENT = "from_client"
    }
}

/**
 * 心跳请求数据
 */
data class HeartbeatRequest(
    val id: String,
    val name: String
)

/**
 * 状态上报数据
 */
data class StatusUpdate(
    val remainingSeconds: Double = 0.0,
    val totalUsageSecondsToday: Double = 0.0,
    val isLocked: Boolean = false,
    val isPaused: Boolean = false
)

/**
 * 消息请求数据
 */
data class MessageRequest(
    val text: String,
    val direction: String = "from_client"
)
