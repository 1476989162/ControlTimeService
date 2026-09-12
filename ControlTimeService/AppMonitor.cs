using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Threading;

namespace ControlTimeService
{
    public class AppMonitor
    {
        // 监控循环跑在独立后台线程。
        // 进程枚举、MainModule 读取、UIAutomation 遍历都是重活（UIA 还是跨进程 COM 调用，
        // 目标进程忙时会阻塞数秒），放在 UI 线程会让主窗口频繁“无响应”。
        private Thread? _monitorThread;
        private volatile bool _monitoring;
        private DateTime _lastLockTriggerTime = DateTime.MinValue;
        private volatile AppPolicy _policy = AppPolicy.CreateDefault();

        // 跨线程写这些字段会导致结构撕裂，统一由监控线程消费重置请求
        private volatile bool _resetTrackingRequested;

        // PID → 进程路径缓存：MainModule 需要打开进程读 PE 头，每秒对全量进程读一次代价极高
        private readonly ConcurrentDictionary<int, ProcessPathEntry> _processPathCache = new();

        // 抖音/豆包/快手/小红书/浏览器 游戏视频前台监控
        private DateTime? _douyinGameForegroundSince;
        private uint _trackedForegroundPid;
        private bool _douyinGameWarningShown;
        private long _enforcementOverrideUntilTicks = DateTime.MinValue.Ticks;
        private string _lastBlockedReason;
        private readonly WindowContentReader _contentReader = new WindowContentReader();

        /// <summary>持续检测到游戏画面至少这么多秒才关闭，给对方反应时间。</summary>
        private const int MinGameVideoCloseSeconds = 10;

        public string LastBlockedReason => _lastBlockedReason;

        // 注意：不要把应用宝/安卓容器/GameAssist 等放进这里。
        // 番茄小说启动也走这些进程；容器是否为游戏只按窗口标题判断。
        private static readonly string[] GameProcessKeywords =
        {
            "steam", "steamwebhelper", "epicgames", "epicwebhelper", "wegame",
            "riotclient", "leagueclient", "leagueoflegends", "league", "dota", "csgo", "cs2",
            "valorant", "pubg", "fortnite", "roblox", "minecraft", "genshin", "yuanshen",
            "starrail", "honkai", "wutheringwaves", "qqgame",
            "crossfire", "overwatch", "apex", "warframe", "diablo", "maplestory",
            "terraria", "stardewvalley", "mumu", "nemu", "dnplayer",
            "ldplayer", "ldvbox", "leidian", "nox", "bluestacks", "hd-player"
            // 注意：不要加 memu —— 逍遥模拟器（MEmu）允许使用，不按游戏拦截
        };

        private static readonly string[] AppStoreContainerProcesses =
        {
            "androws", "aow_rootfs", "aow_exe", "aow_", "AndroidEmulator", "qmemulator",
            "pcyyb", "appmarket", "YYBMarket", "TencentAppStore", "yyb",
            "txgame", "TGB", "GameAssist", "MobileGamePC", "tvm", "torus",
            "exagear", "syzs", "tp3helper", "qmaccelerator", "aow_gpu"
        };

        // 仅匹配窗口标题的宽松特征。不要拿去匹配进程名：
        // 例如 "game" 会误伤大量非游戏进程。
        private static readonly string[] GameTitleProcessHints =
        {
            "game"
        };

        private static readonly string[] GameTitleKeywords =
        {
            "王者荣耀", "原神", "和平精英", "吃鸡", "英雄联盟", "LOL", "DOTA", "CS2", "CS:GO",
            "Steam", "Minecraft", "我的世界", "Roblox", "Fortnite", "鸣潮", "崩坏",
            "第五人格", "蛋仔派对", "明日方舟", "无畏契约", "瓦罗兰特", "永劫无间",
            "金铲铲", "三角洲行动", "使命召唤", "穿越火线", "DNF", "游戏大厅",
            "腾讯手游助手", "手游助手", "QQ游戏", "腾讯游戏", "游戏中心", "网易云游戏",
            "4399", "7k7k"
        };

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        // 必须显式指定 Unicode，否则走 GetWindowTextA，
        // 非中文区域设置下窗口标题会乱码，导致中文关键词全部匹配失败。
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private sealed class ProcessPathEntry
        {
            public string Name = string.Empty;
            public string Path = string.Empty;
        }

        /// <summary>
        /// PID → 该进程首个可见顶层窗口标题。用 EnumWindows 一次遍历完成，
        /// 替代对每个进程调用 Process.MainWindowTitle（内部会重复枚举窗口，代价极高）。
        /// </summary>
        private static Dictionary<int, string> SnapshotTopLevelWindowTitles()
        {
            var map = new Dictionary<int, string>();

            try
            {
                EnumWindows((hwnd, lParam) =>
                {
                    try
                    {
                        if (!IsWindowVisible(hwnd))
                            return true;

                        GetWindowThreadProcessId(hwnd, out uint pid);
                        if (pid == 0)
                            return true;

                        int key = (int)pid;
                        if (map.ContainsKey(key))
                            return true;

                        var sb = new StringBuilder(512);
                        GetWindowText(hwnd, sb, sb.Capacity);
                        if (sb.Length > 0)
                            map[key] = sb.ToString();
                    }
                    catch
                    {
                        // 单个窗口读取失败不影响整体枚举
                    }

                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
                // 枚举失败时返回空映射：仍可凭进程名判断，只是缺少标题维度
            }

            return map;
        }

        /// <summary>
        /// 取进程可执行文件路径。MainModule 需要打开进程句柄并读取 PE 头，
        /// 对受保护进程还会超时；路径在进程存活期间不变，因此按 PID 缓存。
        /// </summary>
        private string GetProcessPathCached(Process process, int pid, string processName)
        {
            if (_processPathCache.TryGetValue(pid, out var cached) &&
                string.Equals(cached.Name, processName, StringComparison.OrdinalIgnoreCase))
            {
                return cached.Path;
            }

            string path = string.Empty;
            try { path = process.MainModule?.FileName ?? string.Empty; } catch { }

            _processPathCache[pid] = new ProcessPathEntry { Name = processName, Path = path };
            return path;
        }

        /// <summary>清理已退出进程的缓存，避免 PID 复用后读到上一个进程的路径。</summary>
        private void PruneProcessPathCache(HashSet<int> alivePids)
        {
            if (_processPathCache.Count <= alivePids.Count)
                return;

            foreach (var pid in _processPathCache.Keys.ToArray())
            {
                if (!alivePids.Contains(pid))
                    _processPathCache.TryRemove(pid, out _);
            }
        }

        public void SetPolicy(AppPolicy policy)
        {
            _policy = policy ?? AppPolicy.CreateDefault();
            _resetTrackingRequested = true;
            Debug.WriteLine($"应用策略已更新: AllowWeChatMiniGames={_policy.AllowWeChatMiniGames}, " +
                $"AllowOtherGames={_policy.AllowOtherGames}, " +
                $"AllowXiaohongshu={_policy.AllowXiaohongshu}, " +
                $"BlockDouyinGameVideos={_policy.BlockDouyinGameVideos}, " +
                $"Threshold={_policy.DouyinGameVideoThresholdSeconds}");
        }

        public void SetEnforcementOverride(DateTime? until)
        {
            Interlocked.Exchange(ref _enforcementOverrideUntilTicks, (until ?? DateTime.MinValue).Ticks);
            _resetTrackingRequested = true;
        }

        private bool IsEnforcementOverridden()
        {
            var until = new DateTime(Interlocked.Read(ref _enforcementOverrideUntilTicks));
            return DateTime.Now < until;
        }

        public void StartMonitoring()
        {
            if (_monitoring)
                return;

            _monitoring = true;
            _monitorThread = new Thread(MonitorLoop)
            {
                IsBackground = true,
                Name = "AppMonitor",
                Priority = ThreadPriority.BelowNormal
            };
            _monitorThread.Start();
        }

        public void StopMonitoring()
        {
            _monitoring = false;
            try { _monitorThread?.Join(1500); } catch { }
            _monitorThread = null;
            _contentReader.Dispose();
        }

        /// <summary>
        /// 后台监控循环：单线程串行执行，天然不会重入；睡眠切片便于快速停止。
        /// </summary>
        private void MonitorLoop()
        {
            while (_monitoring)
            {
                try
                {
                    MonitorTick(null, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"监控循环异常: {ex.Message}");
                }

                for (int i = 0; i < 10 && _monitoring; i++)
                    Thread.Sleep(100);
            }
        }

        private void MonitorTick(object sender, EventArgs e)
        {
            // 策略变更/授权变更带来的状态重置统一在这里执行，避免 UI 线程写监控状态
            if (_resetTrackingRequested)
            {
                _resetTrackingRequested = false;
                ResetDouyinGameTracking();
            }

            if (IsEnforcementOverridden())
                return;

            string blockedAppName = null;

            // 一次性建立 PID → 顶层窗口标题 映射。
            // Process.MainWindowTitle 内部要为每个进程枚举窗口，全量进程逐条读取非常慢。
            var windowTitles = SnapshotTopLevelWindowTitles();
            var alivePids = new HashSet<int>();

            foreach (var process in Process.GetProcesses())
            {
                // Process 对象持有系统句柄，不释放会每秒泄漏一批，
                // 长时间运行后句柄耗尽，表现就是程序越来越卡直至无响应。
                using (process)
                {
                    try
                    {
                        if (process.HasExited)
                            continue;

                        int pid = process.Id;
                        alivePids.Add(pid);

                        string processName = process.ProcessName;
                        if (string.IsNullOrEmpty(processName))
                            continue;

                        string windowTitle = windowTitles.TryGetValue(pid, out var title)
                            ? title
                            : string.Empty;

                        string processPath = GetProcessPathCached(process, pid, processName);
                        if (TryGetBlockReason(processName, windowTitle, out string reason, processPath))
                        {
                            Debug.WriteLine($"命中应用限制: {reason}");
                            _lastBlockedReason = $"{DateTime.Now:HH:mm:ss} {reason}";
                            TerminateProcess(process);
                            blockedAppName ??= reason;
                        }
                    }
                    catch
                    {
                        // 进程可能已退出或无权限访问
                    }
                }
            }

            PruneProcessPathCache(alivePids);

            if (!string.IsNullOrEmpty(blockedAppName))
            {
                TriggerLockScreen(blockedAppName);
            }

            CheckForegroundWeChatMiniGame();
            CheckForegroundGameVideo();
        }

        private void CheckForegroundGameVideo()
        {
            if (!_policy.BlockDouyinGameVideos)
            {
                ResetDouyinGameTracking();
                return;
            }

            // 抖音全禁时仍监控：豆包 / 快手 / 小红书 / 浏览器短视频
            bool needDouyinTrack = _policy.AllowDouyin;
            bool needDoubaoTrack = _policy.MonitorDoubao;
            bool needKuaishouTrack = _policy.AllowKuaishou;
            bool needXiaohongshuTrack = _policy.AllowXiaohongshu;
            bool needBrowserTrack = true;

            if (!needDouyinTrack && !needDoubaoTrack && !needKuaishouTrack && !needXiaohongshuTrack && !needBrowserTrack)
            {
                ResetDouyinGameTracking();
                return;
            }

            Process process = null;
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
                bool isDouyin = IsDouyin(processName, windowTitle);
                bool isDoubao = IsDoubao(processName, windowTitle);
                bool isKuaishou = IsKuaishou(processName, windowTitle);
                bool isXiaohongshu = IsXiaohongshu(processName, windowTitle);
                bool isBrowser = GameVideoContentDetector.IsBrowserProcess(processName);

                bool isTargetApp =
                    (needDouyinTrack && isDouyin) ||
                    (needDoubaoTrack && isDoubao) ||
                    (needKuaishouTrack && isKuaishou) ||
                    (needXiaohongshuTrack && isXiaohongshu) ||
                    (needBrowserTrack && isBrowser);

                if (!isTargetApp)
                {
                    ResetDouyinGameTracking();
                    return;
                }

                // 浏览器：先用标题判断是否短视频站；若不像则做一次轻量 UIA（地址栏），再决定是否深采样
                bool browserInShortVideoContext = false;
                if (isBrowser)
                {
                    browserInShortVideoContext =
                        GameVideoContentDetector.LooksLikeShortVideoPlatform(windowTitle);

                    // 轻量采样地址栏 / 控件，确认是否在抖音/快手等页面
                    _contentReader.Sample(hwnd, enableUia: true, enableOcr: false);
                    string earlyDeep = _contentReader.GetCombinedDeepText(windowTitle);
                    if (GameVideoContentDetector.LooksLikeShortVideoPlatform(earlyDeep))
                        browserInShortVideoContext = true;

                    if (!browserInShortVideoContext)
                    {
                        // 普通网页：不 OCR，避免误伤与性能开销
                        ResetDouyinGameTracking();
                        return;
                    }
                }

                // 目标 App / 短视频网页：启用 UIA + OCR
                _contentReader.Sample(hwnd, enableUia: true, enableOcr: true);

                string uiaText = _contentReader.UiaText;
                string ocrText = _contentReader.OcrText;
                string browserUrl = _contentReader.BrowserUrl;
                string combined = string.Join(" ", windowTitle, browserUrl, uiaText, ocrText);

                // 楚新钓等白名单：立即放行并重置计时
                if (GameVideoContentDetector.ContainsAllowedException(combined))
                {
                    ResetDouyinGameTracking(keepContentCache: true);
                    return;
                }

                bool isGameContent = GameVideoContentDetector.IsGameVideoContent(
                    processName,
                    windowTitle,
                    string.Join(" ", uiaText, browserUrl),
                    ocrText,
                    isDouyin,
                    isDoubao,
                    isKuaishou,
                    isBrowser,
                    isXiaohongshu);

                // 兼容旧逻辑：快手/小红书标题关键词
                if (!isGameContent && needKuaishouTrack && isKuaishou)
                    isGameContent = IsKuaishouGameContent(processName, combined);
                if (!isGameContent && needXiaohongshuTrack && isXiaohongshu)
                    isGameContent = IsKuaishouGameContent(processName, combined);

                if (!isGameContent)
                {
                    // 深度文本尚未就绪时（OCR 异步），对短视频 App 保持观察但不累计，
                    // 避免「标题无关键词」时永远无法进入计时；仅在已有任一样本且无命中时重置。
                    bool deepReady = !string.IsNullOrWhiteSpace(uiaText) || !string.IsNullOrWhiteSpace(ocrText);
                    if (deepReady || isBrowser)
                    {
                        ResetDouyinGameTracking(keepContentCache: true);
                    }
                    return;
                }

                if (_douyinGameForegroundSince == null || _trackedForegroundPid != pid)
                {
                    _douyinGameForegroundSince = DateTime.Now;
                    _trackedForegroundPid = pid;
                    _douyinGameWarningShown = false;
                    var startThreshold = GetGameVideoCloseThresholdSeconds();
                    Debug.WriteLine($"游戏视频计时开始: {processName} | hit={GameVideoContentDetector.FindMatchedKeyword(combined)} | title={windowTitle} | threshold={startThreshold}s");
                    // 一开始就提醒，留出完整反应时间
                    OnDouyinGameVideoWarning?.Invoke(this, new DouyinGameVideoWarningEventArgs
                    {
                        AppName = processName,
                        WindowTitle = windowTitle,
                        RemainingSeconds = startThreshold
                    });
                    return;
                }

                var elapsed = (DateTime.Now - _douyinGameForegroundSince.Value).TotalSeconds;
                var threshold = GetGameVideoCloseThresholdSeconds();

                // 剩约一半时间再提醒一次
                if (!_douyinGameWarningShown && elapsed >= Math.Max(1, threshold / 2.0))
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
                    string appLabel =
                        isBrowser ? "浏览器游戏视频" :
                        isXiaohongshu ? "小红书游戏视频" :
                        isKuaishou ? "快手游戏视频" :
                        isDoubao ? "豆包内游戏视频" : "抖音游戏视频";
                    var matched = GameVideoContentDetector.FindMatchedKeyword(combined);
                    var reason = string.IsNullOrEmpty(matched)
                        ? $"{appLabel}: {windowTitle}"
                        : $"{appLabel}[{matched}]: {windowTitle}";

                    Debug.WriteLine($"命中游戏视频限制: {reason}, continuous={elapsed:F1}s, threshold={threshold}s");
                    _lastBlockedReason = $"{DateTime.Now:HH:mm:ss} {reason}";
                    TerminateProcess(process);
                    ResetDouyinGameTracking();
                    TriggerLockScreen(reason);
                }
            }
            catch
            {
                ResetDouyinGameTracking();
            }
            finally
            {
                process?.Dispose();
            }
        }

        private int GetGameVideoCloseThresholdSeconds()
        {
            // 至少连续 10 秒，避免配置里写成 3 秒导致来不及反应
            return Math.Max(MinGameVideoCloseSeconds, _policy.DouyinGameVideoThresholdSeconds);
        }

        private void ResetDouyinGameTracking(bool keepContentCache = false)
        {
            _douyinGameForegroundSince = null;
            _trackedForegroundPid = 0;
            _douyinGameWarningShown = false;
            if (!keepContentCache)
                _contentReader.Reset();
        }

        private static string GetWindowTitle(IntPtr hwnd)
        {
            var sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static bool IsAppStoreContainerProcess(string processName)
        {
            return AppStoreContainerProcesses.Any(p =>
                processName.Contains(p, StringComparison.OrdinalIgnoreCase));
        }

        private bool TryGetBlockReason(string processName, string windowTitle, out string reason, string processPath = null)
        {
            // 已明确允许的应用直接放行，避免被应用宝容器、ByteDance 进程、游戏等规则误伤
            if (IsExplicitlyAllowedApp(processName, windowTitle, processPath))
            {
                reason = null;
                return false;
            }

            // 允许番茄时：应用宝容器进程一律放行，除非窗口标题能确认是游戏。
            // 解决「畅听能开、小说双击没反应」——小说启动瞬间标题未就绪，却被 GameAssist/aow 进程规则秒杀。
            if (_policy.AllowFanqieNovel && IsAppStoreContainerProcess(processName))
            {
                if (IsClearAppStoreGameTitle(windowTitle))
                {
                    reason = $"应用宝游戏: {processName} - {windowTitle}";
                    return true;
                }

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

            if (!_policy.AllowXiaohongshu && IsXiaohongshu(processName, windowTitle))
            {
                reason = $"小红书: {windowTitle}";
                return true;
            }

            if (!_policy.AllowMaoxiang && IsMaoxiang(processName, windowTitle))
            {
                reason = $"猫箱: {windowTitle}";
                return true;
            }

            if (!_policy.AllowFanqieNovel && IsFanqieNovel(processName, windowTitle, processPath))
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
            if (IsTencentAppStoreGame(processName, windowTitle, processPath))
            {
                reason = $"应用宝游戏: {processName} - {windowTitle}";
                return true;
            }

            if (!_policy.AllowTencentAppStore && IsTencentAppStore(processName, windowTitle, processPath))
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

        private bool IsClearAppStoreGameTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return false;

            string[] strongGameSignals = { "腾讯手游助手", "手游助手", "游戏中", "游戏大厅" };
            if (strongGameSignals.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return true;

            return IsGameTitle(title);
        }

        private bool IsExplicitlyAllowedApp(string processName, string windowTitle, string processPath = null)
        {
            // 逍遥模拟器始终放行（不按“其他游戏”拦截）
            if (IsXiaoyaoEmulator(processName, windowTitle, processPath))
                return true;
            if (_policy.AllowMaoxiang && IsMaoxiang(processName, windowTitle))
                return true;
            if (_policy.AllowFanqieNovel && IsFanqieNovel(processName, windowTitle, processPath))
                return true;
            if (_policy.AllowKuaishou && IsKuaishou(processName, windowTitle))
                return true;
            if (_policy.AllowXiaohongshu && IsXiaohongshu(processName, windowTitle))
                return true;
            // 不在此处调用 IsTencentAppStore，避免与 IsTencentAppStoreGame 形成递归
            return false;
        }

        /// <summary>逍遥模拟器（MEmu / Microvirt），允许使用，不当作游戏。</summary>
        private static bool IsXiaoyaoEmulator(string processName, string title, string processPath = null)
        {
            string[] processHints =
            {
                "memu", "MEmu", "MemuService", "MEmuHeadless", "MEmuConsole",
                "MEmuSVC", "MEmuPlayer", "microvirt"
            };
            if (!string.IsNullOrWhiteSpace(processName) &&
                processHints.Any(p => processName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                return true;

            if (!string.IsNullOrWhiteSpace(title) &&
                (title.Contains("逍遥", StringComparison.OrdinalIgnoreCase) ||
                 title.Contains("MEmu", StringComparison.OrdinalIgnoreCase)))
                return true;

            if (!string.IsNullOrWhiteSpace(processPath) &&
                (processPath.Contains("Microvirt", StringComparison.OrdinalIgnoreCase) ||
                 processPath.Contains("\\MEmu", StringComparison.OrdinalIgnoreCase) ||
                 processPath.Contains("/MEmu", StringComparison.OrdinalIgnoreCase)))
                return true;

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

        /// <summary>
        /// 微信主进程 / 小程序宿主（含新版 Weixin.exe、WeChatAppEx）。
        /// </summary>
        private static bool IsWeChatProcess(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
                return false;

            string[] hints =
            {
                "WeChat", "Weixin", "WeChatApp", "WeChatAppEx", "WeChatBrowser",
                "WeChatPlayer", "WeChatOCR", "WeChatUtility"
            };
            return hints.Any(h => processName.Contains(h, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 小程序专用宿主进程（PC 微信开小程序/小游戏几乎都走这里）。
        /// </summary>
        private static bool IsWeChatMiniProgramHost(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
                return false;

            string[] hosts = { "WeChatAppEx", "WeChatApp", "WeChatBrowser" };
            return hosts.Any(h => processName.Contains(h, StringComparison.OrdinalIgnoreCase));
        }

        private static readonly string[] WeChatMiniGameKeywords =
        {
            "游戏", "小游戏", "小程序游戏", "微信小游戏", "欢乐", "斗地主", "麻将", "棋牌",
            "消消乐", "跳一跳", "坦克", "射击", "跑酷", "拼图", "答题", "猜谜", "益智", "休闲",
            "羊了个羊", "合成大西瓜", "贪吃蛇", "俄罗斯方块", "消除", "闯关", "排位",
            "开始游戏", "重新开始", "再玩一次", "最高分", "排行榜", "复活", "通关",
            "关卡", "体力", "秒玩", "即玩", "试玩", "对战", "联机", "房间号",
            "王者", "吃鸡", "原神", "蛋仔", "迷你世界", "第五人格", "金铲铲"
        };

        private static bool ContainsWeChatMiniGameSignal(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            if (WeChatMiniGameKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return true;

            // 复用通用游戏标题 / 游戏视频关键词，覆盖常见手游名
            if (GameTitleKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return true;

            return GameVideoContentDetector.ContainsGameKeyword(text);
        }

        private bool IsWeChatMiniGame(string title, string processName)
        {
            if (!IsWeChatProcess(processName))
                return false;

            // PC 微信小游戏/小程序都在 WeChatAppEx 等宿主里跑。
            // 禁止小游戏时：宿主一旦有可见窗口标题就拦截（聊天仍走 Weixin/WeChat 主进程）。
            // 否则仅靠游戏名关键词会漏掉大量无「游戏」二字的小程序。
            if (IsWeChatMiniProgramHost(processName) && !string.IsNullOrWhiteSpace(title))
                return true;

            // 主进程窗口标题带游戏特征（旧版或嵌套窗口）
            return ContainsWeChatMiniGameSignal(title);
        }

        /// <summary>
        /// 前台微信/小程序：用 UIA+OCR 补标题检测不到的小游戏（标题常为游戏名或仍显示「微信」）。
        /// </summary>
        private void CheckForegroundWeChatMiniGame()
        {
            if (_policy.AllowWeChatMiniGames)
                return;

            Process process = null;
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                    return;

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0)
                    return;

                string windowTitle = GetWindowTitle(hwnd);
                try
                {
                    process = Process.GetProcessById((int)pid);
                }
                catch
                {
                    return;
                }

                if (process.HasExited)
                    return;

                string processName = process.ProcessName;
                if (!IsWeChatProcess(processName))
                    return;

                // 标题已能定罪时由 MonitorTick 处理；此处专注深度采样
                if (IsWeChatMiniGame(windowTitle, processName))
                    return;

                bool isHost = IsWeChatMiniProgramHost(processName);
                bool titleHintsMiniProgram =
                    !string.IsNullOrWhiteSpace(windowTitle) &&
                    (windowTitle.Contains("小程序", StringComparison.OrdinalIgnoreCase) ||
                     windowTitle.Contains("小游戏", StringComparison.OrdinalIgnoreCase) ||
                     windowTitle.Contains("Mini Program", StringComparison.OrdinalIgnoreCase));

                // 小游戏几乎都在 WeChatAppEx 宿主；主聊天窗不 OCR，避免误伤与性能开销
                if (!isHost && !titleHintsMiniProgram)
                    return;

                _contentReader.Sample(hwnd, enableUia: true, enableOcr: true);
                string combined = _contentReader.GetCombinedDeepText(windowTitle);
                if (!ContainsWeChatMiniGameSignal(combined))
                    return;

                var matched = WeChatMiniGameKeywords.FirstOrDefault(k =>
                    combined.Contains(k, StringComparison.OrdinalIgnoreCase))
                    ?? GameVideoContentDetector.FindMatchedKeyword(combined)
                    ?? windowTitle;
                var reason = $"微信小游戏[{matched}]: {windowTitle}";
                Debug.WriteLine($"命中微信小游戏(深度): {reason}");
                _lastBlockedReason = $"{DateTime.Now:HH:mm:ss} {reason}";
                TerminateProcess(process);

                // 小程序宿主常多开：顺带清理其他宿主进程
                if (isHost)
                    TerminateWeChatMiniProgramHosts();

                TriggerLockScreen(reason);
            }
            catch
            {
                // 忽略瞬时前台切换异常
            }
            finally
            {
                process?.Dispose();
            }
        }

        private static void TerminateWeChatMiniProgramHosts()
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (p.HasExited)
                            continue;
                        if (!IsWeChatMiniProgramHost(p.ProcessName))
                            continue;
                        p.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
        }

        private bool IsDouyin(string processName, string title)
        {
            // 不把 ByteDance 单独当抖音：番茄小说等字节系应用也会用该前缀
            string[] douyinProcesses = { "douyin", "Douyin", "aweme" };
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

        private static bool IsXiaohongshu(string processName, string title)
        {
            return GameVideoContentDetector.IsXiaohongshu(processName, title);
        }

        private bool IsKuaishouGameContent(string processName, string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return false;

            if (GameVideoContentDetector.ContainsAllowedException(title))
                return false;

            return GameVideoContentDetector.ContainsGameKeyword(title);
        }

        private bool IsDoubao(string processName, string title)
        {
            string[] doubaoProcesses = { "doubao", "Doubao", "flow", "byteflow", "coze" };
            if (doubaoProcesses.Any(p => processName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                return true;

            return title.Contains("豆包", StringComparison.OrdinalIgnoreCase);
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

        private bool IsFanqieNovel(string processName, string title, string processPath = null)
        {
            string[] fanqieProcesses = {
                "fanqie", "fanqienovel", "dragonread", "novelfm",
                "hongguo", "changdu", "drread", "fqreader", "tomatovideo",
                "com.dragon.read", "com.phoenix.read"
            };
            if (fanqieProcesses.Any(p => processName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                return true;

            string[] fanqieTitleKeywords = {
                "番茄小说", "番茄畅听", "番茄免费小说", "番茄听书",
                "红果短剧", "红果", "常读", "番茄"
            };
            if (fanqieTitleKeywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return true;

            // 安装路径/命令行中的番茄包名（应用宝容器常见）
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                string[] pathHints = {
                    "fanqie", "dragonread", "dragon.read", "novelfm",
                    "hongguo", "com.dragon.read", "com.phoenix.read", "番茄"
                };
                if (pathHints.Any(p => processPath.Contains(p, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }

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
                if (GameVideoContentDetector.IsBrowserProcess(processName))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsTencentAppStore(string processName, string title, string processPath = null)
        {
            if (_policy.AllowFanqieNovel && IsFanqieNovel(processName, title, processPath))
                return false;

            string[] storeProcesses = { "pcyyb", "appmarket", "YYBMarket", "TencentAppStore" };
            if (storeProcesses.Any(p => processName.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                                        processName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                return !IsTencentAppStoreGame(processName, title, processPath);
            }

            string[] storeTitleKeywords = { "腾讯应用宝", "应用宝" };
            if (storeTitleKeywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                return !IsTencentAppStoreGame(processName, title, processPath);
            }

            return false;
        }

        private bool IsTencentAppStoreGame(string processName, string title, string processPath = null)
        {
            // 允许的猫箱/番茄不按应用宝游戏拦截（勿回调 IsExplicitlyAllowedApp，避免递归）
            if (_policy.AllowMaoxiang && IsMaoxiang(processName, title))
                return false;
            if (_policy.AllowFanqieNovel && IsFanqieNovel(processName, title, processPath))
                return false;

            if (!IsAppStoreContainerProcess(processName))
                return false;

            // 标题能明确证明是游戏才拦截（进程名不足以定罪）
            return IsClearAppStoreGameTitle(title);
        }

        private bool IsGameTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return false;

            return GameTitleKeywords.Any(keyword => title.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                   GameProcessKeywords.Any(gp => title.Contains(gp, StringComparison.OrdinalIgnoreCase)) ||
                   GameTitleProcessHints.Any(gp => title.Contains(gp, StringComparison.OrdinalIgnoreCase));
        }

        private bool IsOtherGame(string processName, string title)
        {
            if (IsXiaoyaoEmulator(processName, title))
                return false;

            if (GameProcessKeywords.Any(gp => processName.Contains(gp, StringComparison.OrdinalIgnoreCase)))
                return true;

            return IsGameTitle(title);
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
