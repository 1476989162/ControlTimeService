using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace ControlTimeService
{
    public class AppMonitor
    {
        private DispatcherTimer _monitorTimer;
        private DateTime _lastLockTriggerTime = DateTime.MinValue;
        private AppPolicy _policy = AppPolicy.CreateDefault();

        // 抖音/豆包游戏视频前台监控
        private DateTime? _douyinGameForegroundSince;
        private uint _trackedForegroundPid;
        private bool _douyinGameWarningShown;

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        public void SetPolicy(AppPolicy policy)
        {
            _policy = policy ?? AppPolicy.CreateDefault();
            ResetDouyinGameTracking();
        }

        public void StartMonitoring()
        {
            _monitorTimer = new DispatcherTimer();
            _monitorTimer.Interval = TimeSpan.FromSeconds(1);
            _monitorTimer.Tick += MonitorTick;
            _monitorTimer.Start();
        }

        public void StopMonitoring()
        {
            _monitorTimer?.Stop();
        }

        private void MonitorTick(object sender, EventArgs e)
        {
            string blockedAppName = null;

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.HasExited)
                        continue;

                    string processName = process.ProcessName;
                    string windowTitle = process.MainWindowTitle ?? string.Empty;

                    if (string.IsNullOrEmpty(processName))
                        continue;

                    if (TryGetBlockReason(processName, windowTitle, out string reason))
                    {
                        TerminateProcess(process);
                        blockedAppName ??= reason;
                    }
                }
                catch
                {
                    // 进程可能已退出或无权限访问
                }
            }

            if (!string.IsNullOrEmpty(blockedAppName))
            {
                TriggerLockScreen(blockedAppName);
            }

            CheckForegroundDouyinGameVideo();
        }

        private void CheckForegroundDouyinGameVideo()
        {
            if (!_policy.BlockDouyinGameVideos)
            {
                ResetDouyinGameTracking();
                return;
            }

            // 抖音全禁时仍可能需要监控快手游戏视频；仅当抖音/豆包/快手都不需要前台监控时才退出
            bool needDouyinTrack = _policy.AllowDouyin;
            bool needDoubaoTrack = _policy.MonitorDoubao;
            bool needKuaishouTrack = _policy.AllowKuaishou;
            if (!needDouyinTrack && !needDoubaoTrack && !needKuaishouTrack)
            {
                ResetDouyinGameTracking();
                return;
            }

            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                {
                    ResetDouyinGameTracking();
                    return;
                }

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0)
                {
                    ResetDouyinGameTracking();
                    return;
                }

                string windowTitle = GetWindowTitle(hwnd);
                Process process;
                try
                {
                    process = Process.GetProcessById((int)pid);
                }
                catch
                {
                    ResetDouyinGameTracking();
                    return;
                }

                if (process.HasExited)
                {
                    ResetDouyinGameTracking();
                    return;
                }

                string processName = process.ProcessName;
                bool isTargetApp =
                    (needDouyinTrack && IsDouyin(processName, windowTitle)) ||
                    (needDoubaoTrack && IsDoubao(processName, windowTitle)) ||
                    (needKuaishouTrack && IsKuaishou(processName, windowTitle));

                if (!isTargetApp)
                {
                    ResetDouyinGameTracking();
                    return;
                }

                bool isGameContent = IsDouyinGameContent(processName, windowTitle) ||
                    (needKuaishouTrack && IsKuaishou(processName, windowTitle) && IsKuaishouGameContent(processName, windowTitle));

                if (!isGameContent)
                {
                    ResetDouyinGameTracking();
                    return;
                }

                if (_douyinGameForegroundSince == null || _trackedForegroundPid != pid)
                {
                    _douyinGameForegroundSince = DateTime.Now;
                    _trackedForegroundPid = pid;
                    _douyinGameWarningShown = false;
                    return;
                }

                var elapsed = (DateTime.Now - _douyinGameForegroundSince.Value).TotalSeconds;
                var threshold = Math.Max(1, _policy.DouyinGameVideoThresholdSeconds);

                if (!_douyinGameWarningShown && elapsed >= Math.Max(1, threshold - 5))
                {
                    _douyinGameWarningShown = true;
                    var remaining = Math.Max(1, (int)Math.Ceiling(threshold - elapsed));
                    OnDouyinGameVideoWarning?.Invoke(this, new DouyinGameVideoWarningEventArgs
                    {
                        AppName = processName,
                        WindowTitle = windowTitle,
                        RemainingSeconds = remaining
                    });
                }

                if (elapsed >= threshold)
                {
                    string appLabel = IsKuaishou(processName, windowTitle) ? "快手游戏视频" :
                        IsDoubao(processName, windowTitle) ? "豆包内游戏视频" : "抖音游戏视频";
                    var reason = $"{appLabel}: {windowTitle}";

                    TerminateProcess(process);
                    ResetDouyinGameTracking();
                    TriggerLockScreen(reason);
                }
            }
            catch
            {
                ResetDouyinGameTracking();
            }
        }

        private void ResetDouyinGameTracking()
        {
            _douyinGameForegroundSince = null;
            _trackedForegroundPid = 0;
            _douyinGameWarningShown = false;
        }

        private static string GetWindowTitle(IntPtr hwnd)
        {
            var sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private bool TryGetBlockReason(string processName, string windowTitle, out string reason)
        {
            // 已明确允许的应用直接放行，避免被应用宝容器、ByteDance 进程、游戏等规则误伤
            if (IsExplicitlyAllowedApp(processName, windowTitle))
            {
                reason = null;
                return false;
            }

            if (!_policy.AllowWeChatMiniGames && IsWeChatMiniGame(windowTitle, processName))
            {
                reason = $"微信小游戏: {windowTitle}";
                return true;
            }

            if (!_policy.AllowDouyin && IsDouyin(processName, windowTitle))
            {
                reason = $"抖音: {windowTitle}";
                return true;
            }

            if (!_policy.AllowKuaishou && IsKuaishou(processName, windowTitle))
            {
                reason = $"快手: {windowTitle}";
                return true;
            }

            if (!_policy.AllowMaoxiang && IsMaoxiang(processName, windowTitle))
            {
                reason = $"猫箱: {windowTitle}";
                return true;
            }

            if (!_policy.AllowFanqieNovel && IsFanqieNovel(processName, windowTitle))
            {
                reason = $"番茄小说: {windowTitle}";
                return true;
            }

            if (!_policy.AllowVideo && IsVideo(processName, windowTitle))
            {
                reason = $"视频: {processName} - {windowTitle}";
                return true;
            }

            // 应用宝内游戏：始终拦截（猫箱、番茄在 IsExplicitlyAllowedApp 和 IsTencentAppStoreGame 内部已排除）
            if (IsTencentAppStoreGame(processName, windowTitle))
            {
                reason = $"应用宝游戏: {processName} - {windowTitle}";
                return true;
            }

            if (!_policy.AllowTencentAppStore && IsTencentAppStore(processName, windowTitle))
            {
                reason = $"腾讯应用宝: {windowTitle}";
                return true;
            }

            if (!_policy.AllowOtherGames && IsOtherGame(processName, windowTitle))
            {
                reason = $"游戏: {processName} - {windowTitle}";
                return true;
            }

            reason = null;
            return false;
        }

        private bool IsExplicitlyAllowedApp(string processName, string windowTitle)
        {
            if (_policy.AllowMaoxiang && IsMaoxiang(processName, windowTitle))
                return true;
            if (_policy.AllowFanqieNovel && IsFanqieNovel(processName, windowTitle))
                return true;
            if (_policy.AllowKuaishou && IsKuaishou(processName, windowTitle))
                return true;
            // 不在此处调用 IsTencentAppStore，避免与 IsTencentAppStoreGame 形成递归
            return false;
        }

        private void TerminateProcess(Process process)
        {
            try
            {
                if (process.HasExited)
                    return;

                Debug.WriteLine($"终止进程: {process.ProcessName} (PID {process.Id})");

                if (!string.IsNullOrEmpty(process.MainWindowTitle))
                {
                    process.CloseMainWindow();
                    if (process.WaitForExit(300))
                        return;
                }

                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 部分系统进程无权限终止
            }
        }

        private void TriggerLockScreen(string appName)
        {
            if ((DateTime.Now - _lastLockTriggerTime).TotalSeconds < 3)
                return;

            _lastLockTriggerTime = DateTime.Now;
            Debug.WriteLine($"触发锁屏: {appName}");
            OnAppBlocked?.Invoke(this, new AppBlockedEventArgs { AppName = appName });
        }

        private bool IsWeChatMiniGame(string title, string processName)
        {
            if (!processName.Contains("WeChat", StringComparison.OrdinalIgnoreCase))
                return false;

            string[] gameKeywords = {
                "游戏", "小游戏", "欢乐", "斗地主", "麻将", "棋牌",
                "消消乐", "跳一跳", "坦克", "射击", "跑酷", "拼图",
                "答题", "猜谜", "益智", "休闲"
            };

            return gameKeywords.Any(keyword => title.Contains(keyword));
        }

        private bool IsDouyin(string processName, string title)
        {
            string[] douyinProcesses = { "douyin", "Douyin", "aweme", "ByteDance" };
            if (douyinProcesses.Any(p => processName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                return true;

            return title.Contains("抖音", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsKuaishou(string processName, string title)
        {
            string[] kuaishouProcesses = { "kuaishou", "Kuaishou", "kwai", "Kwai", "ksapp", "KSApp", "kwailive", "KwaiLive" };
            if (kuaishouProcesses.Any(p => processName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                return true;

            return title.Contains("快手", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsKuaishouGameContent(string processName, string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return false;

            string[] gameKeywords = {
                "游戏", "小游戏", "秒玩", "即玩", "试玩", "休闲", "闯关",
                "快手游戏", "快手小游戏", "即点即玩", "电竞", "手游", "端游",
                "王者", "原神", "吃鸡", "和平精英", "英雄联盟", "LOL",
                "实况", "攻略", "解说", "通关", "战绩", "排位", "Steam",
                "minecraft", "我的世界", "蛋仔", "蛋仔派对", "迷你世界", "第五人格",
                "明日方舟", "崩坏", "阴阳师", "火影", "CF", "DNF"
            };

            return gameKeywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase));
        }

        private bool IsDoubao(string processName, string title)
        {
            string[] doubaoProcesses = { "doubao", "Doubao", "flow", "byteflow", "coze" };
            if (doubaoProcesses.Any(p => processName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                return true;

            return title.Contains("豆包", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsDouyinGameContent(string processName, string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return false;

            string[] gameKeywords = {
                "游戏", "小游戏", "秒玩", "即玩", "试玩", "休闲", "闯关",
                "抖音游戏", "抖音小游戏", "即点即玩", "电竞", "手游", "端游",
                "王者", "原神", "吃鸡", "和平精英", "英雄联盟", "LOL",
                "实况", "攻略", "解说", "通关", "战绩", "排位", "Steam",
                "minecraft", "我的世界", "蛋仔", "蛋仔派对", "迷你世界", "第五人格",
                "明日方舟", "崩坏", "阴阳师", "火影", "CF", "DNF",
                "楚心钓", "楚新钓","修狗","地铁逃生","我开始贼溜"
            };

            if (gameKeywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return true;

            // 豆包内嵌抖音页面：标题含抖音且含视频/推荐等浏览特征时视为需监控
            if (IsDoubao(processName, title) &&
                title.Contains("抖音", StringComparison.OrdinalIgnoreCase) &&
                (title.Contains("视频", StringComparison.OrdinalIgnoreCase) ||
                 title.Contains("推荐", StringComparison.OrdinalIgnoreCase) ||
                 title.Contains("直播", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return false;
        }

        private bool IsMaoxiang(string processName, string title)
        {
            string[] maoxiangProcesses = {
                "maoxiang", "Maoxiang", "catbox", "CatBox", "miaohezi",
                "parallel", "Parallel", "odyssey", "Odyssey",
                "iredwhale", "IredWhale", "huolu"
            };
            if (maoxiangProcesses.Any(p => processName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                return true;

            string[] maoxiangTitleKeywords = { "猫箱", "话炉" };
            return maoxiangTitleKeywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase));
        }

        private bool IsFanqieNovel(string processName, string title)
        {
            string[] fanqieProcesses = {
                "fanqie", "fanqienovel", "dragonread", "novelfm",
                "hongguo", "changdu", "drread", "fqreader"
            };
            if (fanqieProcesses.Any(p => processName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                return true;

            string[] fanqieTitleKeywords = {
                "番茄小说", "番茄畅听", "番茄免费小说", "红果短剧", "红果", "常读", "番茄"
            };
            if (fanqieTitleKeywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return true;

            // 应用宝容器内：仅当标题能确认是番茄系内容时才放行，避免把容器里的游戏当成番茄
            return false;
        }

        private bool IsVideo(string processName, string title)
        {
            string[] videoProcesses = {
                "bilibili", "iQIYI", "iqiyi", "Youku", "youku", "QQPlayer",
                "PotPlayer", "PotPlayerMini", "wmplayer", "mpv", "vlc",
                "TencentVideo", "QQLive", "StormPlayer", "Baofeng"
            };

            if (videoProcesses.Any(p => processName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                return true;

            string[] videoTitleKeywords = { "哔哩哔哩", "Bilibili", "爱奇艺", "优酷", "腾讯视频", "视频" };
            if (videoTitleKeywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                if (processName.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
                    processName.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
                    processName.Contains("firefox", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsTencentAppStore(string processName, string title)
        {
            if (_policy.AllowFanqieNovel && IsFanqieNovel(processName, title))
                return false;

            string[] storeProcesses = { "pcyyb", "appmarket", "YYBMarket", "TencentAppStore" };
            if (storeProcesses.Any(p => processName.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                                        processName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                return !IsTencentAppStoreGame(processName, title);
            }

            string[] storeTitleKeywords = { "腾讯应用宝", "应用宝" };
            if (storeTitleKeywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                return !IsTencentAppStoreGame(processName, title);
            }

            return false;
        }

        private bool IsTencentAppStoreGame(string processName, string title)
        {
            // 允许的猫箱/番茄不按应用宝游戏拦截（勿回调 IsExplicitlyAllowedApp，避免递归）
            if (_policy.AllowMaoxiang && IsMaoxiang(processName, title))
                return false;
            if (_policy.AllowFanqieNovel && IsFanqieNovel(processName, title))
                return false;

            string[] dedicatedGameProcesses = { "txgame", "TGB", "GameAssist", "MobileGamePC" };
            if (dedicatedGameProcesses.Any(gp => processName.Contains(gp, StringComparison.OrdinalIgnoreCase)))
                return true;

            string[] containerProcesses = {
                "androws", "aow_rootfs", "aow_exe", "AndroidEmulator", "qmemulator",
                "pcyyb", "appmarket", "YYBMarket"
            };
            if (!containerProcesses.Any(gp => processName.Contains(gp, StringComparison.OrdinalIgnoreCase)))
            {
                string[] gameTitleKeywords = { "腾讯手游助手", "手游助手", "正在运行", "游戏中" };
                if (gameTitleKeywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    if (processName.Contains("pcyyb", StringComparison.OrdinalIgnoreCase) ||
                        processName.Contains("appmarket", StringComparison.OrdinalIgnoreCase) ||
                        processName.Contains("androws", StringComparison.OrdinalIgnoreCase) ||
                        processName.Contains("txgame", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            }

            // 应用宝容器也用于番茄小说、猫箱等非游戏 App，需结合标题/游戏特征判断
            string[] gameSignals = { "腾讯手游助手", "手游助手", "正在运行", "游戏中" };
            if (gameSignals.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return true;

            return IsOtherGame(processName, title);
        }

        private bool IsOtherGame(string processName, string title)
        {
            string[] gameProcesses = {
                "steam", "epicgames", "unity", "unreal",
                "game", "league", "dota", "csgo", "minecraft",
                "roblox", "fortnite", "pubg", "valorant"
            };

            return gameProcesses.Any(gp =>
                processName.Contains(gp, StringComparison.OrdinalIgnoreCase) ||
                title.Contains(gp, StringComparison.OrdinalIgnoreCase));
        }

        public event EventHandler<AppBlockedEventArgs> OnAppBlocked;
        public event EventHandler<DouyinGameVideoWarningEventArgs> OnDouyinGameVideoWarning;
    }

    public class AppBlockedEventArgs : EventArgs
    {
        public string AppName { get; set; }
    }

    public class DouyinGameVideoWarningEventArgs : EventArgs
    {
        public string AppName { get; set; }
        public string WindowTitle { get; set; }
        public int RemainingSeconds { get; set; }
    }
}
