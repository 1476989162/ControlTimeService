using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

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
        public DayOfWeek Day { get; set; }
        public int UsageMinutes { get; set; }      // 使用时长（分钟）
        public int RestMinutes { get; set; }       // 休息时长（分钟）
        public bool Enabled { get; set; }          // 是否启用
        
        // 午间规则
        public bool LunchRestrictionEnabled { get; set; }
        public int LunchMaxUsageMinutes { get; set; }  // 午间最大使用时长
        public string LunchStartTime { get; set; }   // 午间开始时间 (HH:mm)
        public string LunchEndTime { get; set; }     // 午间结束时间 (HH:mm)
        
        // 晚间规则
        public bool EveningRestrictionEnabled { get; set; }
        public int EveningMaxUsageMinutes { get; set; }
        public string EveningStartTime { get; set; }
        public string EveningEndTime { get; set; }
        
        // 夜间关机时间
        public string NightShutdownTime { get; set; } = "20:30";

        // 早晨锁定（解锁时间前强制锁屏）
        public bool MorningLockEnabled { get; set; } = false;
        public string MorningUnlockTime { get; set; } = "08:00";

        // 应用权限（每天独立配置）
        public bool AllowVideo { get; set; } = false;
        public bool AllowWeChatMiniGames { get; set; } = false;
        public bool AllowMaoxiang { get; set; } = true;
        public bool AllowDouyin { get; set; } = false;
        public bool AllowFanqieNovel { get; set; } = true;
        public bool AllowTencentAppStore { get; set; } = true;
        public bool AllowOtherGames { get; set; } = false;

        // 抖音游戏视频监控（AllowDouyin=true 时仍拦截游戏内容）
        public bool BlockDouyinGameVideos { get; set; } = true;
        public int DouyinGameVideoThresholdSeconds { get; set; } = 10;
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
            result.UsageMinutes = incoming.UsageMinutes;
            result.RestMinutes = incoming.RestMinutes;
            result.Enabled = incoming.Enabled;

            // 逐字段合并：只要 incoming 提供了有效值就覆盖。
            // 不再以单个字段（如 LunchStartTime）作为整体开关，
            // 避免部分下发（Web 端、轻量编辑器）导致扩展配置被忽略而不生效。
            result.LunchRestrictionEnabled = incoming.LunchRestrictionEnabled;
            result.LunchMaxUsageMinutes = incoming.LunchMaxUsageMinutes;
            if (!string.IsNullOrWhiteSpace(incoming.LunchStartTime))
                result.LunchStartTime = incoming.LunchStartTime;
            if (!string.IsNullOrWhiteSpace(incoming.LunchEndTime))
                result.LunchEndTime = incoming.LunchEndTime;
            result.EveningRestrictionEnabled = incoming.EveningRestrictionEnabled;
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

        public DaySchedule GetScheduleForToday()
        {
            return _schedules[DateTime.Now.DayOfWeek];
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
                    Enabled = true,
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
        /// 从旧版 app_policy.json 迁移全局应用权限到所有 DaySchedule
        /// </summary>
        private void MigrateAppPolicyFromLegacyFile()
        {
            try
            {
                var legacyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_policy.json");
                if (!File.Exists(legacyPath))
                    return;

                var legacyJson = File.ReadAllText(legacyPath);
                var legacyPolicy = JsonSerializer.Deserialize<AppPolicy>(legacyJson);
                if (legacyPolicy == null)
                    return;

                foreach (var schedule in _schedules.Values)
                {
                    schedule.AllowVideo = legacyPolicy.AllowVideo;
                    schedule.AllowWeChatMiniGames = legacyPolicy.AllowWeChatMiniGames;
                    schedule.AllowMaoxiang = legacyPolicy.AllowMaoxiang;
                    schedule.AllowDouyin = legacyPolicy.AllowDouyin;
                    schedule.AllowFanqieNovel = legacyPolicy.AllowFanqieNovel;
                    schedule.AllowTencentAppStore = legacyPolicy.AllowTencentAppStore;
                    schedule.AllowOtherGames = legacyPolicy.AllowOtherGames;
                }

                SaveToFile();
                System.Diagnostics.Debug.WriteLine($"已从 {legacyPath} 迁移应用权限到每天配置");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"迁移旧版应用权限失败: {ex.Message}");
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
