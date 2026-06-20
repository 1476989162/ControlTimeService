using System;
using System.Text.Json.Serialization;

namespace ControlTimeService
{
    public class AppPolicy
    {
        /// <summary>是否允许观看视频（B站、爱奇艺、优酷等）</summary>
        [JsonPropertyName("allowVideo")]
        public bool AllowVideo { get; set; }

        /// <summary>是否允许微信小游戏</summary>
        [JsonPropertyName("allowWeChatMiniGames")]
        public bool AllowWeChatMiniGames { get; set; }

        /// <summary>是否允许猫箱</summary>
        [JsonPropertyName("allowMaoxiang")]
        public bool AllowMaoxiang { get; set; }

        /// <summary>是否允许抖音</summary>
        [JsonPropertyName("allowDouyin")]
        public bool AllowDouyin { get; set; }

        /// <summary>是否允许番茄小说</summary>
        [JsonPropertyName("allowFanqieNovel")]
        public bool AllowFanqieNovel { get; set; }

        /// <summary>是否允许腾讯应用宝（仅商店客户端，不含其内游戏）</summary>
        [JsonPropertyName("allowTencentAppStore")]
        public bool AllowTencentAppStore { get; set; }

        /// <summary>是否允许其他游戏（Steam 等）</summary>
        [JsonPropertyName("allowOtherGames")]
        public bool AllowOtherGames { get; set; }

        /// <summary>是否拦截抖音/豆包中的游戏视频（AllowDouyin=true 时仍生效）</summary>
        [JsonPropertyName("blockDouyinGameVideos")]
        public bool BlockDouyinGameVideos { get; set; } = true;

        /// <summary>游戏视频连续观看超过此秒数后关闭应用</summary>
        [JsonPropertyName("douyinGameVideoThresholdSeconds")]
        public int DouyinGameVideoThresholdSeconds { get; set; } = 10;

        /// <summary>是否监控豆包内打开的抖音内容</summary>
        [JsonPropertyName("monitorDoubao")]
        public bool MonitorDoubao { get; set; } = true;

        public static AppPolicy CreateDefault()
        {
            return new AppPolicy
            {
                AllowVideo = false,
                AllowWeChatMiniGames = false,
                AllowMaoxiang = true,
                AllowDouyin = false,
                AllowFanqieNovel = true,
                AllowTencentAppStore = true,
                AllowOtherGames = false,
                BlockDouyinGameVideos = true,
                DouyinGameVideoThresholdSeconds = 10,
                MonitorDoubao = true
            };
        }

        public AppPolicy Clone()
        {
            return new AppPolicy
            {
                AllowVideo = AllowVideo,
                AllowWeChatMiniGames = AllowWeChatMiniGames,
                AllowMaoxiang = AllowMaoxiang,
                AllowDouyin = AllowDouyin,
                AllowFanqieNovel = AllowFanqieNovel,
                AllowTencentAppStore = AllowTencentAppStore,
                AllowOtherGames = AllowOtherGames,
                BlockDouyinGameVideos = BlockDouyinGameVideos,
                DouyinGameVideoThresholdSeconds = DouyinGameVideoThresholdSeconds,
                MonitorDoubao = MonitorDoubao
            };
        }
    }

    public class AppPolicyManager
    {
        private readonly TimeConfigManager _configManager;

        public AppPolicyManager(TimeConfigManager configManager)
        {
            _configManager = configManager;
        }

        /// <summary>获取今天的应用权限策略</summary>
        public AppPolicy GetPolicy()
        {
            var today = _configManager.GetScheduleForToday();
            return today.ToAppPolicy();
        }

        /// <summary>获取指定日期的应用权限策略</summary>
        public AppPolicy GetPolicyForDay(DayOfWeek day)
        {
            var schedule = _configManager.GetSchedule(day);
            return schedule.ToAppPolicy();
        }

        /// <summary>更新今天的应用权限（保存到当天配置中）</summary>
        public void UpdatePolicy(AppPolicy policy)
        {
            if (policy == null) return;
            var today = DateTime.Now.DayOfWeek;
            var schedule = _configManager.GetSchedule(today);
            ApplyPolicyToSchedule(schedule, policy);
            _configManager.UpdateSchedule(today, schedule);
        }

        /// <summary>从远程更新今天的应用权限</summary>
        public void UpdateFromRemote(AppPolicy remotePolicy)
        {
            if (remotePolicy == null) return;
            var today = DateTime.Now.DayOfWeek;
            var schedule = _configManager.GetSchedule(today);
            ApplyPolicyToSchedule(schedule, remotePolicy);
            _configManager.UpdateSchedule(today, schedule);
        }

        private static void ApplyPolicyToSchedule(DaySchedule schedule, AppPolicy policy)
        {
            schedule.AllowVideo = policy.AllowVideo;
            schedule.AllowWeChatMiniGames = policy.AllowWeChatMiniGames;
            schedule.AllowMaoxiang = policy.AllowMaoxiang;
            schedule.AllowDouyin = policy.AllowDouyin;
            schedule.AllowFanqieNovel = policy.AllowFanqieNovel;
            schedule.AllowTencentAppStore = policy.AllowTencentAppStore;
            schedule.AllowOtherGames = policy.AllowOtherGames;
            schedule.BlockDouyinGameVideos = policy.BlockDouyinGameVideos;
            schedule.DouyinGameVideoThresholdSeconds = policy.DouyinGameVideoThresholdSeconds;
            schedule.MonitorDoubao = policy.MonitorDoubao;
        }
    }
}
