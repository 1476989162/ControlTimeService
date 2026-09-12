package com.controltime.android.data

import androidx.room.Entity
import androidx.room.PrimaryKey

/**
 * 客户端注册信息
 */
@Entity(tableName = "client_info")
data class ClientInfoEntity(
    @PrimaryKey val clientId: String,
    val deviceName: String = "",
    val serverUrl: String = "",
    val lastHeartbeat: Long = 0,
    val isRegistered: Boolean = false
)
