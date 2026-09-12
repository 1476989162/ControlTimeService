package com.controltime.android.model

import com.google.gson.annotations.SerializedName
import java.io.Serializable

/**
 * 应用权限策略
 * 对应服务端 AppPolicy 类
 */
data class AppPolicy(
    @SerializedName("allowVideo")
    var allowVideo: Boolean = false,

    @SerializedName("allowWeChatMiniGames")
    var allowWeChatMiniGames: Boolean = false,

    @SerializedName("allowMaoxiang")
    var allowMaoxiang: Boolean = true,

    @SerializedName("allowDouyin")
    var allowDouyin: Boolean = true,

    @SerializedName("allowKuaishou")
    var allowKuaishou: Boolean = true,

    @SerializedName("allowXiaohongshu")
    var allowXiaohongshu: Boolean = true,

    @SerializedName("allowFanqieNovel")
    var allowFanqieNovel: Boolean = true,

    @SerializedName("allowTencentAppStore")
    var allowTencentAppStore: Boolean = true,

    @SerializedName("allowOtherGames")
    var allowOtherGames: Boolean = true,

    @SerializedName("blockDouyinGameVideos")
    var blockDouyinGameVideos: Boolean = true,

    @SerializedName("douyinGameVideoThresholdSeconds")
    var douyinGameVideoThresholdSeconds: Int = 10,

    @SerializedName("monitorDoubao")
    var monitorDoubao: Boolean = true
) : Serializable {

    fun copy(): AppPolicy {
        return AppPolicy(
            allowVideo = allowVideo,
            allowWeChatMiniGames = allowWeChatMiniGames,
            allowMaoxiang = allowMaoxiang,
            allowDouyin = allowDouyin,
            allowKuaishou = allowKuaishou,
            allowXiaohongshu = allowXiaohongshu,
            allowFanqieNovel = allowFanqieNovel,
            allowTencentAppStore = allowTencentAppStore,
            allowOtherGames = allowOtherGames,
            blockDouyinGameVideos = blockDouyinGameVideos,
            douyinGameVideoThresholdSeconds = douyinGameVideoThresholdSeconds,
            monitorDoubao = monitorDoubao
        )
    }
}
