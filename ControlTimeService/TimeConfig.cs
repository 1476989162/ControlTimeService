using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlTimeService
{
    internal static class ConfigJson
    {
        internal static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
    }

    public class DaySchedule
    {
        [JsonPropertyName("day")]
        public DayOfWeek Day { get; set; }
        [JsonPropertyName("usageMinutes")]
        public int UsageMinutes { get; set; }      // 使用时长（分钟）
        [JsonPropertyName("restMinutes")]
        public int RestMinutes { get; set; }       // 休息时长（分钟）
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;          // 是否启用（UI 默认不勾选，但运行时默认启用避免锁死）
        
        // 午间规则
        [JsonPropertyName("lunchRestrictionEnabled")]
        public bool LunchRestrictionEnabled { get; set; }
        [JsonPropertyName("lunchMaxUsageMinutes")]
        public int LunchMaxUsageMinutes { get; set; }  // 午间最大使用时长
        [JsonPropertyName("lunchStartTime")]
        public string LunchStartTime { get; set; }   // 午间开始时间 (HH:mm)
        [JsonPropertyName("lunchEndTime")]
        public string LunchEndTime { get; set; }     // 午间结束时间 (HH:mm)
        
        // 晚间规则
        [JsonPropertyName("eveningRestrictionEnabled")]
        public bool EveningRestrictionEnabled { get; set; }
        [JsonPropertyName("eveningMaxUsageMinutes")]
        public int EveningMaxUsageMinutes { get; set; }
        [JsonPropertyName("eveningStartTime")]
        public string EveningStartTime { get; set; }
        [JsonPropertyName("eveningEndTime")]
        public string EveningEndTime { get; set; }
        
        // 夜间关机时间
        [JsonPropertyName("nightShutdownTime")]
        public string NightShutdownTime { get; set; } = "20:30";

        // 早晨锁定（解锁时间前强制锁屏）
        [JsonPropertyName("morningLockEnabled")]
        public bool MorningLockEnabled { get; set; } = false;
        [JsonPropertyName("morningUnlockTime")]
        public string MorningUnlockTime { get; set; } = "08:00";

        // 应用权限（每天独立配置）
        [JsonPropertyName("allowVideo")]
        public bool AllowVideo { get; set; } = false;
        [JsonPropertyName("allowWeChatMiniGames")]
        public bool AllowWeChatMiniGames { get; set; } = true;
        [JsonPropertyName("allowMaoxiang")]
        public bool AllowMaoxiang { get; set; } = true;
        [JsonPropertyName("allowDouyin")]
        public bool AllowDouyin { get; set; } = true;
        [JsonPropertyName("allowKuaishou")]
        public bool AllowKuaishou { get; set; } = true;
        [JsonPropertyName("allowFanqieNovel")]
        public bool AllowFanqieNovel { get; set; } = true;
        [JsonPropertyName("allowTencentAppStore")]
        public bool AllowTencentAppStore { get; set; } = true;
        [JsonPropertyName("allowOtherGames")]
        public bool AllowOtherGames { get; set; } = true;

        // 抖音游戏视频监控（AllowDouyin=true 时仍拦截游戏内容）
        [JsonPropertyName("blockDouyinGameVideos")]
        public bool BlockDouyinGameVideos { get; set; } = true;
        [JsonPropertyName("douyinGameVideoThresholdSeconds")]
        public int DouyinGameVideoThresholdSeconds { get; set; } = 10;
        [JsonPropertyName("monitorDoubao")]
        public bool MonitorDoubao { get; set; } = true;

        /// <summary>提取当天的 AppPolicy（用于向后兼容和监控）</summary>
        public AppPolicy ToAppPolicy()
        {
            return new AppPolicy
            {
                AllowVideo = AllowVideo,
                AllowWeChatMiniGames = AllowWeChatMiniGames,
                AllowMaoxiang = AllowMaoxiang,
                AllowDouyin = AllowDouyin,
                AllowKuaishou = AllowKuaishou,
                AllowFanqieNovel = AllowFanqieNovel,
                AllowTencentAppStore = AllowTencentAppStore,
                AllowOtherGames = AllowOtherGames,
                BlockDouyinGameVideos = BlockDouyinGameVideos,
                DouyinGameVideoThresholdSeconds = DouyinGameVideoThresholdSeconds,
                MonitorDoubao = MonitorDoubao
            };
        }

        public DaySchedule Clone()
        {
            return new DaySchedule
            {
                Day = Day,
                UsageMinutes = UsageMinutes,
                RestMinutes = RestMinutes,
                Enabled = Enabled,
                LunchRestrictionEnabled = LunchRestrictionEnabled,
                LunchMaxUsageMinutes = LunchMaxUsageMinutes,
                LunchStartTime = LunchStartTime,
                LunchEndTime = LunchEndTime,
                EveningRestrictionEnabled = EveningRestrictionEnabled,
                EveningMaxUsageMinutes = EveningMaxUsageMinutes,
                EveningStartTime = EveningStartTime,
                EveningEndTime = EveningEndTime,
                NightShutdownTime = NightShutdownTime,
                MorningLockEnabled = MorningLockEnabled,
                MorningUnlockTime = MorningUnlockTime,
                AllowVideo = AllowVideo,
                AllowWeChatMiniGames = AllowWeChatMiniGames,
                AllowMaoxiang = AllowMaoxiang,
                AllowDouyin = AllowDouyin,
                AllowKuaishou = AllowKuaishou,
                AllowFanqieNovel = AllowFanqieNovel,
                AllowTencentAppStore = AllowTencentAppStore,
                AllowOtherGames = AllowOtherGames,
                BlockDouyinGameVideos = BlockDouyinGameVideos,
                DouyinGameVideoThresholdSeconds = DouyinGameVideoThresholdSeconds,
                MonitorDoubao = MonitorDoubao
            };
        }

        /// <summary>
        /// 将 incoming 合并到 existing：始终更新基础时长字段；若 incoming 含完整时段配置则覆盖扩展字段。
        /// </summary>
        public static DaySchedule Merge(DaySchedule existing, DaySchedule incoming)
        {
            if (incoming == null)
                return existing?.Clone() ?? new DaySchedule();

            var result = (existing ?? new DaySchedule()).Clone();
            if (incoming.UsageMinutes > 0)
                result.UsageMinutes = incoming.UsageMinutes;
            if (incoming.RestMinutes > 0)
                result.RestMinutes = incoming.RestMinutes;
            result.Enabled = incoming.Enabled;

            // 逐字段合并：只要 incoming 提供了有效值就覆盖。
            // 不再以单个字段（如 LunchStartTime）作为整体开关，
            // 避免部分下发（Web 端、轻量编辑器）导致扩展配置被忽略而不生效。
            result.LunchRestrictionEnabled = incoming.LunchRestrictionEnabled;
            // 允许下发 0（关闭上限）；仅跳过未提供的负值哨兵
            if (incoming.LunchMaxUsageMinutes >= 0)
                result.LunchMaxUsageMinutes = incoming.LunchMaxUsageMinutes;
            if (!string.IsNullOrWhiteSpace(incoming.LunchStartTime))
                result.LunchStartTime = incoming.LunchStartTime;
            if (!string.IsNullOrWhiteSpace(incoming.LunchEndTime))
                result.LunchEndTime = incoming.LunchEndTime;
            result.EveningRestrictionEnabled = incoming.EveningRestrictionEnabled;
            if (incoming.EveningMaxUsageMinutes >= 0)
                result.EveningMaxUsageMinutes = incoming.EveningMaxUsageMinutes;
            if (!string.IsNullOrWhiteSpace(incoming.EveningStartTime))
                result.EveningStartTime = incoming.EveningStartTime;
            if (!string.IsNullOrWhiteSpace(incoming.EveningEndTime))
                result.EveningEndTime = incoming.EveningEndTime;
            if (!string.IsNullOrWhiteSpace(incoming.NightShutdownTime))
                result.NightShutdownTime = incoming.NightShutdownTime;
            result.MorningLockEnabled = incoming.MorningLockEnabled;
            if (!string.IsNullOrWhiteSpace(incoming.MorningUnlockTime))
                result.MorningUnlockTime = incoming.MorningUnlockTime;

            result.AllowVideo = incoming.AllowVideo;
            result.AllowWeChatMiniGames = incoming.AllowWeChatMiniGames;
            result.AllowMaoxiang = incoming.AllowMaoxiang;
            result.AllowDouyin = incoming.AllowDouyin;
            result.AllowKuaishou = incoming.AllowKuaishou;
            result.AllowFanqieNovel = incoming.AllowFanqieNovel;
            result.AllowTencentAppStore = incoming.AllowTencentAppStore;
            result.AllowOtherGames = incoming.AllowOtherGames;
            result.BlockDouyinGameVideos = incoming.BlockDouyinGameVideos;
            if (incoming.DouyinGameVideoThresholdSeconds > 0)
                result.DouyinGameVideoThresholdSeconds = incoming.DouyinGameVideoThresholdSeconds;
            result.MonitorDoubao = incoming.MonitorDoubao;

            return result;
        }

        public TimeSpan GetLunchStartTime()
        {
            return TimeSpan.TryParse(LunchStartTime, out var ts) ? ts : new TimeSpan(11, 0, 0);
        }

        public TimeSpan GetLunchEndTime()
        {
            return TimeSpan.TryParse(LunchEndTime, out var ts) ? ts : new TimeSpan(14, 0, 0);
        }

        public TimeSpan GetEveningStartTime()
        {
            return TimeSpan.TryParse(EveningStartTime, out var ts) ? ts : new TimeSpan(18, 0, 0);
        }

        public TimeSpan GetEveningEndTime()
        {
            return TimeSpan.TryParse(EveningEndTime, out var ts) ? ts : new TimeSpan(20, 30, 0);
        }

        public TimeSpan GetNightShutdownTime()
        {
            return TimeSpan.TryParse(NightShutdownTime, out var ts) ? ts : new TimeSpan(20, 30, 0);
        }

        public TimeSpan GetMorningUnlockTime()
        {
            return TimeSpan.TryParse(MorningUnlockTime, out var ts) ? ts : new TimeSpan(8, 0, 0);
        }
    }

    public class TimeConfigManager
    {
        private Dictionary<DayOfWeek, DaySchedule> _schedules;
        private string _configPath;

        public TimeConfigManager()
        {
            _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "time_config.json");
            LoadDefaultConfig();
            LoadFromFile();
        }

        /// <summary>
        /// 是否处于暑假模式（每年 7月1日 ~ 8月31日）。
        /// 暑假模式下覆盖当天配置：30分钟解锁/60分钟休息循环，8:00-20:30 可用，其余时段锁屏。
        /// </summary>
        public static bool IsSummerMode(DateTime now)
        {
            return now >= new DateTime(now.Year, 7, 1) && now < new DateTime(now.Year, 9, 1);
        }

        /// <summary>将暑假规则覆盖到给定 schedule 上（不修改应用权限字段）。</summary>
        public static void ApplySummerOverride(DaySchedule schedule)
        {
            if (schedule == null) return;
            schedule.UsageMinutes = 30;
            schedule.RestMinutes = 60;
            schedule.MorningLockEnabled = true;
            schedule.MorningUnlockTime = "08:00";
            schedule.NightShutdownTime = "20:30";
            // 暑假模式由 8:00-20:30 整体窗口控制，禁用午间/晚间细分规则避免冲突
            schedule.LunchRestrictionEnabled = false;
            schedule.EveningRestrictionEnabled = false;
        }

        public DaySchedule GetScheduleForToday()
        {
            var schedule = _schedules[DateTime.Now.DayOfWeek].Clone();
            if (IsSummerMode(DateTime.Now))
            {
                ApplySummerOverride(schedule);
            }
            return schedule;
        }

        public DaySchedule GetSchedule(DayOfWeek day)
        {
            return _schedules[day];
        }

        public void UpdateSchedule(DayOfWeek day, DaySchedule schedule)
        {
            _schedules[day] = schedule?.Clone() ?? new DaySchedule { Day = day };
            _schedules[day].Day = day;
            SaveToFile();
        }

        public Dictionary<string, DaySchedule> GetAllSchedules()
        {
            var result = new Dictionary<string, DaySchedule>();
            foreach (var kvp in _schedules)
            {
                result[kvp.Key.ToString()] = kvp.Value;
            }
            return result;
        }

        public void UpdateFromRemote(Dictionary<string, DaySchedule> remoteConfig)
        {
            if (remoteConfig == null)
                return;

            foreach (var kvp in remoteConfig)
            {
                if (Enum.TryParse<DayOfWeek>(kvp.Key, out var day))
                {
                    var existing = _schedules.TryGetValue(day, out var current) ? current : new DaySchedule { Day = day };
                    _schedules[day] = DaySchedule.Merge(existing, kvp.Value);
                    _schedules[day].Day = day;
                }
            }
            SaveToFile();
        }

        public static Dictionary<string, DaySchedule> MergeConfig(
            Dictionary<string, DaySchedule> existing,
            Dictionary<string, DaySchedule> incoming)
        {
            var result = new Dictionary<string, DaySchedule>();
            if (existing != null)
            {
                foreach (var kvp in existing)
                    result[kvp.Key] = kvp.Value?.Clone() ?? new DaySchedule();
            }

            if (incoming == null)
                return result;

            foreach (var kvp in incoming)
            {
                if (!Enum.TryParse<DayOfWeek>(kvp.Key, out var day))
                    continue;

                var current = result.TryGetValue(kvp.Key, out var existingDay)
                    ? existingDay
                    : new DaySchedule { Day = day };
                result[kvp.Key] = DaySchedule.Merge(current, kvp.Value);
                result[kvp.Key].Day = day;
            }

            return result;
        }

        private void LoadDefaultConfig()
        {
            // 加载默认配置（保持原有逻辑）
            _schedules = new Dictionary<DayOfWeek, DaySchedule>();
            foreach (DayOfWeek day in Enum.GetValues(typeof(DayOfWeek)))
            {
                _schedules[day] = new DaySchedule
                {
                    Day = day,
                    UsageMinutes = 30,
                    RestMinutes = day >= DayOfWeek.Monday && day <= DayOfWeek.Friday ? 30 : 45,
                    // 默认不启用：新装客户端只有家长明确开启当天控制后才进入正常计时。
                    Enabled = false,
                    LunchRestrictionEnabled = true,
                    LunchMaxUsageMinutes = 60,
                    LunchStartTime = "11:00",
                    LunchEndTime = "14:00",
                    EveningRestrictionEnabled = true,
                    EveningMaxUsageMinutes = 30,
                    EveningStartTime = "18:00",
                    EveningEndTime = "20:30",
                    NightShutdownTime = "20:30"
                };
            }
        }

        private void LoadFromFile()
        {
            if (File.Exists(_configPath))
            {
                try
                {
                    var json = File.ReadAllText(_configPath);
                    var config = JsonSerializer.Deserialize<Dictionary<string, DaySchedule>>(json, ConfigJson.Options);
                    
                    if (config != null)
                    {
                        foreach (var kvp in config)
                        {
                            if (Enum.TryParse<DayOfWeek>(kvp.Key, out var day))
                            {
                                _schedules[day] = kvp.Value;
                            }
                        }

                        // 向后兼容：从旧版 app_policy.json 迁移应用权限到每天配置
                        MigrateAppPolicyFromLegacyFile();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"加载配置文件失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 从旧版 app_policy.json 迁移全局应用权限到所有 DaySchedule。
        /// 仅在 time_config 尚未包含应用权限字段时迁移一次，随后删除遗留文件，
        /// 避免每次重启用旧策略覆盖已保存的番茄/抖音等权限。
        /// </summary>
        private void MigrateAppPolicyFromLegacyFile()
        {
            try
            {
                var legacyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_policy.json");
                if (!File.Exists(legacyPath))
                    return;

                // time_config 已含应用权限时，说明已迁移或由管理端下发过
                if (File.Exists(_configPath))
                {
                    var existingJson = File.ReadAllText(_configPath);
                    if (existingJson.IndexOf("allowFanqieNovel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        existingJson.IndexOf("allowDouyin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        existingJson.IndexOf("allowMaoxiang", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // 修复历史 bug：旧 app_policy 无番茄字段时，每次启动都会把 AllowFanqieNovel 刷成 false
                        var legacyText = File.ReadAllText(legacyPath);
                        if (legacyText.IndexOf("allowFanqieNovel", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            var repaired = false;
                            foreach (var schedule in _schedules.Values)
                            {
                                if (!schedule.AllowFanqieNovel)
                                {
                                    schedule.AllowFanqieNovel = true;
                                    repaired = true;
                                }
                            }
                            if (repaired)
                                SaveToFile();
                        }

                        TryRemoveLegacyAppPolicyFile(legacyPath);
                        return;
                    }
                }

                var legacyJson = File.ReadAllText(legacyPath);
                // 属性已带默认值；旧文件缺字段时不会把番茄等权限写成 false
                var legacyPolicy = JsonSerializer.Deserialize<AppPolicy>(legacyJson) ?? AppPolicy.CreateDefault();

                foreach (var schedule in _schedules.Values)
                {
                    schedule.AllowVideo = legacyPolicy.AllowVideo;
                    schedule.AllowWeChatMiniGames = legacyPolicy.AllowWeChatMiniGames;
                    schedule.AllowMaoxiang = legacyPolicy.AllowMaoxiang;
                    schedule.AllowDouyin = legacyPolicy.AllowDouyin;
                    schedule.AllowKuaishou = legacyPolicy.AllowKuaishou;
                    schedule.AllowFanqieNovel = legacyPolicy.AllowFanqieNovel;
                    schedule.AllowTencentAppStore = legacyPolicy.AllowTencentAppStore;
                    schedule.AllowOtherGames = legacyPolicy.AllowOtherGames;
                    schedule.BlockDouyinGameVideos = legacyPolicy.BlockDouyinGameVideos;
                    schedule.DouyinGameVideoThresholdSeconds = legacyPolicy.DouyinGameVideoThresholdSeconds;
                    schedule.MonitorDoubao = legacyPolicy.MonitorDoubao;
                }

                SaveToFile();
                TryRemoveLegacyAppPolicyFile(legacyPath);
                System.Diagnostics.Debug.WriteLine($"已从 {legacyPath} 迁移应用权限到每天配置");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"迁移旧版应用权限失败: {ex.Message}");
            }
        }

        private static void TryRemoveLegacyAppPolicyFile(string legacyPath)
        {
            try
            {
                if (File.Exists(legacyPath))
                    File.Delete(legacyPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"删除旧版 app_policy.json 失败: {ex.Message}");
            }
        }

        private void SaveToFile()
        {
            try
            {
                var json = JsonSerializer.Serialize(GetAllSchedules(), ConfigJson.Options);
                File.WriteAllText(_configPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存配置文件失败: {ex.Message}");
            }
        }
    }
}
