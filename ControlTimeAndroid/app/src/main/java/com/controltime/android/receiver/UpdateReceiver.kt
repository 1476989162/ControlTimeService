package com.controltime.android.receiver

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import com.controltime.android.service.ControlTimeService

/**
 * APK 更新下载接收器
 */
class UpdateReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        when (intent.action) {
            Intent.ACTION_DOWNLOAD_COMPLETE -> {
                val downloadId = intent.getLongExtra(android.app.DownloadManager.EXTRA_DOWNLOAD_ID, -1L)
                if (downloadId != -1L) {
                    // 下载完成，触发安装
                    // TODO: 安装 APK
                }
            }
        }
    }
}
