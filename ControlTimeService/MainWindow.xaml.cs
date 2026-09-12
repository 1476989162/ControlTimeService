using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Forms; // 需引用 System.Windows.Forms
using System.Windows.Threading;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ControlTimeService
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer _logicTimer = new DispatcherTimer();
        private System.Windows.Threading.DispatcherTimer _statusTimer;
        private System.Windows.Threading.DispatcherTimer _watchdogTimer;

        // 核心变量
        private DateTime _targetTime; // 当前阶段的结束时间点
        private bool _isResting = false; // 当前是否处于”休息模式”
        private bool _isShutdownMode = false; // 夜间关机锁模式（不计时，显示关机按钮）
        private bool _isPasswordRequiredOnly = false; // 晚间仅密码解锁模式
        private bool _isTimingPaused = false; // 暂停计时（锁屏但不消耗时间）
        private double _pausedRemainingSeconds = 0; // 暂停时保存的剩余秒数

        // 日累计统计（用于午间/晚间规则 + 总使用量上报）
        private DateTime _usageStatsDate = DateTime.Today;
        private double _totalDailyUsageSeconds = 0;
        private double _lunchAccumulatedSeconds = 0;
        private int _lunchBreaksTaken = 0;
        private double _eveningAccumulatedSeconds = 0;
        private bool _eveningPasswordBypassActive = false;
        private bool _lunchPasswordBypassActive = false;
        private DateTime _nightShutdownBypassUntil = DateTime.MinValue;
        private DateTime _morningLockBypassUntil = DateTime.MinValue;
        private DateTime _dayDisabledBypassUntil = DateTime.MinValue;
        private bool _isAppViolationPause = false;
        private bool _isMorningLockMode = false;
        private bool _morningPasswordBypassActive = false;
        private DateTime _appBlockCooldownUntil = DateTime.MinValue;
        private DateTime _lastLogicTick = DateTime.Now;
        private readonly ConcurrentQueue<Action> _remoteCommandQueue = new();
        private readonly ConcurrentQueue<Action> _priorityRemoteCommandQueue = new();
        private bool _suppressLockReopen = false;
        private DateTime _remoteUnlockUntil = DateTime.MinValue;
        private string _pendingUpdateUrl;
        private string _pendingUpdateVersion;

        // 开屏/锁屏累计与完整性校验（从每天开机时刻起算）
        private double _lockedSecondsToday = 0;                 // 今日累计锁屏秒（休息/夜间/暂停/早晨锁）
        private DateTime _integrityAnchor = DateTime.MinValue;  // 完整性锚点（开机时刻/零点/重授权）
        private double _countedSecondsAtAnchor = 0;             // 锚点时刻已累计(开屏+锁屏)秒
        private double _sleepGraceSeconds = 0;                  // 锚点后睡眠/休眠豁免秒（不视为缺失）
        private bool _integrityLockActive = false;              // 完整性密码锁进行中
        private DateTime _lastStateSave = DateTime.MinValue;    // 周期性落盘节流
        private const double IntegrityMismatchThresholdSeconds = 600; // 开机至今与(开屏+锁屏)差值超10分钟即锁屏

        private string _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "state.txt");
        private string _adminPass = "123456789"; // 管理密码

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        // 系统本次会话（最近一次开机/快速启动恢复）的开始时刻，启动后计算一次并缓存
        private DateTime? _systemSessionStart = null;
        private bool _systemSessionStartResolved = false;
        private NotifyIcon _notifyIcon;
        private AppMonitor _appMonitor;
        private TimeConfigManager _configManager;
        private AppPolicyManager _appPolicyManager;
        private ControlClient _controlClient;
        private LockWindow _activeLockWindow;
        private AutoUpdater _autoUpdater;
        private ControlConfig _controlConfig;

        public MainWindow()
        {
            InitializeComponent();
            SetAutoStart();
            // 最强看门狗：注册表+计划任务每分钟自检，崩溃/被杀1分钟内自动拉起
            try { CrashLogger.AttachDispatcher(Dispatcher); } catch { }
            // 看门狗安装内部会同步调用 schtasks（查询/创建各可能阻塞数秒），
            // 放到后台线程，避免拖住窗口构造与首屏渲染。
            System.Threading.Tasks.Task.Run(() =>
            {
                try { WatchdogHelper.EnsureInstalled(); }
                catch (Exception ex) { Debug.WriteLine($"看门狗初始化失败: {ex.Message}"); }
            });
            InitNotifyIcon();

            // 初始化配置管理器
            _configManager = new TimeConfigManager();
            _appPolicyManager = new AppPolicyManager(_configManager);

            // 初始化应用监控器
            _appMonitor = new AppMonitor();
            _appMonitor.SetPolicy(_appPolicyManager.GetPolicy());
            _appMonitor.OnAppBlocked += (s, e) =>
            {
                // 监控循环已移到后台线程：必须用 BeginInvoke 异步派发，
                // 用 Invoke 会让后台线程同步等待 UI 线程，锁屏/弹窗时容易互相卡住。
                this.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (DateTime.Now >= _appBlockCooldownUntil &&
                        !IsRemoteUnlockActive(DateTime.Now) &&
                        !_isResting &&
                        !_isTimingPaused &&
                        !_isShutdownMode &&
                        _activeLockWindow == null)
                    {
                        StartAppViolationLock();
                    }

                    _notifyIcon?.ShowBalloonTip(
                        3000,
                        "应用限制",
                        $"检测到禁止的应用：{e.AppName}",
                        ToolTipIcon.Warning);

                    // 推送：违规拦截实时上报控制端（状态里的 lastBlockedApp + 消息记录），
                    // 家长在控制端 10 秒心跳之外也能立刻看到，不用等下一次定时上报。
                    ReportStatusToControl();
                    PushViolationToServer($"【违规拦截 {DateTime.Now:MM-dd HH:mm:ss}】{e.AppName}");
                }));
            };
            _appMonitor.OnDouyinGameVideoWarning += (s, e) =>
            {
                this.Dispatcher.BeginInvoke(new Action(() =>
                {
                    _notifyIcon?.ShowBalloonTip(
                        5000,
                        "游戏视频提醒",
                        $"检测到游戏画面（{e.AppName}），请在 {e.RemainingSeconds} 秒内切走，否则将关闭应用",
                        ToolTipIcon.Warning);

                    // 推送：游戏视频预警同样实时上报，方便家长知晓孩子正在看游戏内容
                    ReportStatusToControl();
                    PushViolationToServer($"【游戏视频提醒 {DateTime.Now:MM-dd HH:mm:ss}】{e.AppName}：{e.WindowTitle}（{e.RemainingSeconds} 秒后关闭）");
                }));
            };
            _appMonitor.StartMonitoring();

            // 初始化控制客户端（如果配置了控制端地址）
            _controlConfig = ControlConfig.Load();
            string controlServerUrl = _controlConfig.ServerUrl;
            if (!string.IsNullOrEmpty(controlServerUrl))
            {
                System.Diagnostics.Debug.WriteLine($"检测到控制端配置: {controlServerUrl}");
                _controlClient = new ControlClient(controlServerUrl, _configManager, _appPolicyManager, this);
                _controlClient.Start();
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("未找到控制端配置文件 (control_config.json)，客户端将以独立模式运行");
            }

            _autoUpdater = new AutoUpdater(_controlConfig, message =>
            {
                // 更新检查在后台线程回调，NotifyIcon 必须在 UI 线程访问
                try
                {
                    this.Dispatcher.BeginInvoke(new Action(() =>
                        _notifyIcon?.ShowBalloonTip(3000, "ControlTimeService 升级", message, ToolTipIcon.Info)));
                }
                catch { }
            });
            _autoUpdater.StartPeriodicCheck();

            // 1. 加载状态
            LoadState();

            // 2. 启动定时器 (每秒刷新一次 UI 和 逻辑)
            _logicTimer.Interval = TimeSpan.FromSeconds(1);
            _logicTimer.Tick += LogicTimer_Tick;
            _lastLogicTick = DateTime.Now;
            _logicTimer.Start();

            // 3. 独立状态上报定时器（每 10 秒上报一次，不受 LogicTimer_Tick 条件影响）
            _statusTimer = new System.Windows.Threading.DispatcherTimer();
            _statusTimer.Interval = TimeSpan.FromSeconds(10);
            _statusTimer.Tick += (s, e) => ReportStatusToControl();
            _statusTimer.Start();

            // 4. 首次启动 1 秒后立即上报一次
            var firstReportTimer = new System.Windows.Threading.DispatcherTimer();
            firstReportTimer.Interval = TimeSpan.FromSeconds(1);
            firstReportTimer.Tick += (s, e) =>
            {
                ((System.Windows.Threading.DispatcherTimer)s).Stop();
                ReportStatusToControl();
            };
            firstReportTimer.Start();

            // 5. 看门狗自愈：每10分钟检查一次，防止任务被手动删除后防护失效
            _watchdogTimer = new System.Windows.Threading.DispatcherTimer();
            _watchdogTimer.Interval = TimeSpan.FromMinutes(10);
            _watchdogTimer.Tick += (s, e) =>
            {
                // 内部 schtasks 查询/创建会阻塞数秒，放后台执行，别占着 UI 线程
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { WatchdogHelper.EnsureInstalledIfNeeded(); } catch { }
                });
            };
            _watchdogTimer.Start();

            // 启动时如果是静默启动可以 Hide，这里为了演示默认 Show，你可以改为 Hide
            // this.Hide(); 
        }

        public void EnqueueRemoteCommand(Action action, bool highPriority = false)
        {
            if (action != null)
            {
                if (highPriority)
                    _priorityRemoteCommandQueue.Enqueue(action);
                else
                    _remoteCommandQueue.Enqueue(action);
            }
        }

        public void ProcessRemoteCommandQueue()
        {
            while (true)
            {
                if (!_priorityRemoteCommandQueue.TryDequeue(out var action) &&
                    !_remoteCommandQueue.TryDequeue(out action))
                    break;

                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"远程命令执行失败: {ex.Message}");
                }
            }
        }

        private bool IsRemoteUnlockActive(DateTime now)
        {
            return now < _remoteUnlockUntil;
        }

        private void ClearExpiredRemoteUnlock(DateTime now)
        {
            if (_remoteUnlockUntil == DateTime.MinValue || now < _remoteUnlockUntil)
                return;

            CancelRemoteUnlockOverride();
        }

        /// <summary>
        /// 取消远程解锁授权并恢复全部自动管控。
        /// 服务端锁定/暂停命令无条件生效时调用（最后下发的服务端命令优先）。
        /// </summary>
        private void CancelRemoteUnlockOverride()
        {
            _remoteUnlockUntil = DateTime.MinValue;
            _appMonitor?.SetEnforcementOverride(null);
            _eveningPasswordBypassActive = false;
            _lunchPasswordBypassActive = false;
            _morningPasswordBypassActive = false;
            _nightShutdownBypassUntil = DateTime.MinValue;
            _morningLockBypassUntil = DateTime.MinValue;
            _dayDisabledBypassUntil = DateTime.MinValue;
        }

        private void SetRemoteUnlockOverride(DateTime until)
        {
            _remoteUnlockUntil = until;
            _nightShutdownBypassUntil = until;
            _morningLockBypassUntil = until;
            _eveningPasswordBypassActive = true;
            _lunchPasswordBypassActive = true;
            _morningPasswordBypassActive = true;
            _appMonitor?.SetEnforcementOverride(until);
        }

        private void LogicTimer_Tick(object sender, EventArgs e)
        {
            ProcessRemoteCommandQueue();

            var now = DateTime.Now;
            ClearExpiredRemoteUnlock(now);
            ResetDailyUsageIfNeeded(now);
            AccumulateUsage(now);
            InitializeIntegrityAnchor(now);

            TryApplyPendingUpdate();

            if (!IsRemoteUnlockActive(now))
            {
                if (IsNightlyShutdownTime(now) && !_isShutdownMode && now > _nightShutdownBypassUntil)
                {
                    StartRestMode(null, true);
                    return;
                }

                if (IsMorningLockTime(now))
                {
                    if (!_morningPasswordBypassActive && !_isShutdownMode && !_isTimingPaused && now > _morningLockBypassUntil)
                    {
                        if (!_isMorningLockMode)
                        {
                            StartMorningLock(now);
                            return;
                        }
                    }
                }
                else
                {
                    _morningPasswordBypassActive = false;
                    if (_isMorningLockMode && !_isResting)
                    {
                        _isMorningLockMode = false;
                    }
                }

                EvaluateTimeWindowRules(now);

                var schedule = _configManager.GetScheduleForToday();
                if (!schedule.Enabled &&
                    !_isResting && !_isShutdownMode && !_isTimingPaused &&
                    now > _dayDisabledBypassUntil)
                {
                    StartRestMode(null, false, true, isDayDisabledLock: true);
                    return;
                }
            }

            // 完整性校验：开机至今 vs (开屏+锁屏)，差值超 10 分钟视为时间记录异常 → 密码锁
            // 睡眠/休眠已由 _sleepGraceSeconds 豁免；进程被强杀/时钟被改都会在这里暴露
            // [临时] 暂时去掉“时间比对不符合”的提示：完整性校验锁已禁用。
            // 需要恢复时，取消下面代码块的注释即可。
            //if (!IsRemoteUnlockActive(now) &&
            //    _activeLockWindow == null &&
            //    !_isResting && !_isShutdownMode && !_isTimingPaused &&
            //    IntegrityMismatch(now))
            //{
            //    StartRestMode(null, false, true, isIntegrityLock: true);
            //    return;
            //}

            // 周期性落盘（每5秒），避免强杀进程/断电丢失累计与锁定状态
            if (_lastStateSave == DateTime.MinValue || (now - _lastStateSave).TotalSeconds >= 5)
            {
                _lastStateSave = now;
                SaveState();
            }

            var remaining = _targetTime - now;

            if (_isTimingPaused)
            {
                StatusLabel.Text = "当前状态：计时已暂停（锁屏中）";
                StatusLabel.Foreground = System.Windows.Media.Brushes.Orange;
                var pausedRemaining = TimeSpan.FromSeconds(_pausedRemainingSeconds);
                TimeLabel.Text = $"暂停剩余：{pausedRemaining.Hours:D2}:{pausedRemaining.Minutes:D2}:{pausedRemaining.Seconds:D2}";
                return;
            }

            if (_isShutdownMode)
            {
                StatusLabel.Text = "当前状态：夜间关机锁定（需管理员或关机）";
                StatusLabel.Foreground = System.Windows.Media.Brushes.Red;
            }
            else if (_isMorningLockMode)
            {
                StatusLabel.Text = "当前状态：早晨锁定中";
                StatusLabel.Foreground = System.Windows.Media.Brushes.Red;
            }
            else if (_isResting)
            {
                StatusLabel.Text = "当前状态：强制休息中";
                StatusLabel.Foreground = System.Windows.Media.Brushes.Red;
            }
            else
            {
                StatusLabel.Text = "当前状态：正常使用中";
                StatusLabel.Foreground = System.Windows.Media.Brushes.Green;
            }

            if (!_isShutdownMode)
            {
                if (remaining.TotalSeconds > 0)
                {
                    TimeLabel.Text = $"剩余时间：{remaining.Hours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
                }
                else
                {
                    TimeLabel.Text = "时间到！正在切换状态...";
                }
            }
            else
            {
                TimeLabel.Text = "已进入夜间关机锁定";
            }

            if (!_isShutdownMode && remaining.TotalSeconds <= 0)
            {
                if (!_isResting)
                {
                    StartRestMode();
                }
                else
                {
                    StartUsageMode();
                }
            }

        }

        private void StartUsageMode(int? customUsageMinutes = null)
        {
            _isResting = false;
            _isShutdownMode = false;
            _isPasswordRequiredOnly = false;
            _isAppViolationPause = false;
            _isMorningLockMode = false;

            int usage, rest;
            GetDurationsForNow(out usage, out rest);

            if (customUsageMinutes.HasValue)
            {
                _targetTime = DateTime.Now.AddMinutes(customUsageMinutes.Value);
            }
            else
            {
                _targetTime = DateTime.Now.AddMinutes(usage);
            }

            CapTargetTimeToLunchAllowance(DateTime.Now);
            SaveState();
        }

        /// <summary>
        /// 开启强制休息模式（根据当天配置）
        /// </summary>
        private void StartRestMode(
            DateTime? customEndTime = null,
            bool isShutdownMode = false,
            bool isPasswordRequiredOnly = false,
            bool isDayDisabledLock = false,
            bool isIntegrityLock = false)
        {
            _isResting = true;
            _isShutdownMode = isShutdownMode;
            _isPasswordRequiredOnly = isPasswordRequiredOnly;
            _isMorningLockMode = false;
            _integrityLockActive = isIntegrityLock;
            _eveningPasswordBypassActive = false;

            if (isShutdownMode)
            {
                _targetTime = DateTime.Now.AddYears(100);
            }
            else if (isPasswordRequiredOnly)
            {
                _targetTime = DateTime.Now.AddYears(100);
            }
            else if (customEndTime.HasValue)
            {
                _targetTime = customEndTime.Value;
            }
            else
            {
                int usage, rest;
                GetDurationsForNow(out usage, out rest);
                _targetTime = DateTime.Now.AddMinutes(rest);
            }

            SaveState();

            OpenLockWindow(_isShutdownMode, isPasswordRequiredOnly, false, isDayDisabledLock: isDayDisabledLock, isIntegrityLock: isIntegrityLock);
            TryApplyPendingUpdate();
        }

        /// <summary>
        /// 早晨锁定：解锁时间前强制锁屏（可配置启用/禁用）
        /// </summary>
        private void StartMorningLock(DateTime now)
        {
            var schedule = _configManager.GetScheduleForToday();
            var unlockTime = now.Date + schedule.GetMorningUnlockTime();

            _isMorningLockMode = true;
            _isResting = true;
            _isShutdownMode = false;
            _isPasswordRequiredOnly = false;
            _isTimingPaused = false;
            _isAppViolationPause = false;
            _targetTime = unlockTime;
            SaveState();

            OpenLockWindow(false, false, false, isMorningLockMode: true);
            TryApplyPendingUpdate();
        }

        /// <summary>
        /// 暂停计时：锁屏并冻结剩余使用时间
        /// </summary>
        private void StartPauseMode()
        {
            if (_isResting || _isShutdownMode || _isTimingPaused)
                return;

            var remaining = (_targetTime - DateTime.Now).TotalSeconds;
            if (remaining < 0) remaining = 0;

            _pausedRemainingSeconds = remaining;
            _isTimingPaused = true;
            _isAppViolationPause = false;
            SaveState();

            OpenLockWindow(false, false, true);
        }

        /// <summary>
        /// 应用违规锁屏：暂停计时，解锁后恢复原有剩余时间
        /// </summary>
        private void StartAppViolationLock()
        {
            if (IsRemoteUnlockActive(DateTime.Now) ||
                _isResting || _isShutdownMode || _isTimingPaused || _activeLockWindow != null)
                return;

            var remaining = (_targetTime - DateTime.Now).TotalSeconds;
            if (remaining < 0) remaining = 0;

            _pausedRemainingSeconds = remaining;
            _isTimingPaused = true;
            _isAppViolationPause = true;
            SaveState();

            OpenLockWindow(false, false, true, requirePasswordForResume: false, isAppViolationPause: true);
        }

        private void ResumeFromPause()
        {
            _isTimingPaused = false;
            _isResting = false;
            _isShutdownMode = false;
            _isPasswordRequiredOnly = false;

            _targetTime = DateTime.Now.AddSeconds(_pausedRemainingSeconds);
            _pausedRemainingSeconds = 0;

            if (_isAppViolationPause)
            {
                _isAppViolationPause = false;
                _appBlockCooldownUntil = DateTime.Now.AddMinutes(2);
            }

            SaveState();
            this.Show();
        }

        private void OpenLockWindow(
            bool isShutdownMode,
            bool isPasswordRequiredOnly,
            bool isPauseMode,
            bool requirePasswordForResume = false,
            bool isAppViolationPause = false,
            bool isMorningLockMode = false,
            bool isDayDisabledLock = false,
            bool isIntegrityLock = false)
        {
            this.Hide();

            var lockWin = new LockWindow(
                _targetTime,
                _adminPass,
                false,
                isShutdownMode,
                () =>
                {
                    _isShutdownMode = true;
                    _isResting = true;
                    _isMorningLockMode = false;
                    _targetTime = DateTime.Now.AddYears(100);
                    SaveState();
                },
                isPasswordRequiredOnly,
                isPauseMode,
                requirePasswordForResume,
                isAppViolationPause,
                ProcessRemoteCommandQueue,
                isMorningLockMode,
                isDayDisabledLock,
                isIntegrityLock);

            _activeLockWindow = lockWin;
            bool? result = null;
            try
            {
                result = lockWin.ShowDialog();
            }
            catch
            {
                result = null;
            }
            finally
            {
                _activeLockWindow = null;
            }

            if (result == true)
            {
                if (lockWin.WasRemoteAbort)
                {
                    // 远程命令已接管状态，此处不做额外处理
                }
                else if (lockWin.WasPauseUnlock)
                {
                    ResumeFromPause();
                }
                else
                {
                    if (_isTimingPaused)
                    {
                        _isTimingPaused = false;
                        _isAppViolationPause = false;
                        _pausedRemainingSeconds = 0;
                    }

                    if (lockWin.WasPasswordOnlyUnlock)
                    {
                        // 完整性锁通过密码解锁：重新锚定，继续正常计时
                        if (_integrityLockActive)
                        {
                            _integrityLockActive = false;
                            ReanchorIntegrity();
                        }

                        _eveningPasswordBypassActive = true;
                        _lunchPasswordBypassActive = true;
                        // 防止夜间关机时间和早晨锁定立刻重新锁定
                        var bypassDuration = TimeSpan.FromMinutes(lockWin.TemporaryUsageMinutes ?? 30);
                        _nightShutdownBypassUntil = DateTime.Now.Add(bypassDuration);
                        _morningLockBypassUntil = DateTime.Now.Add(bypassDuration);
                        // 当天未启用时紧急解锁，避免下一秒又因 Enabled=false 立刻重锁
                        _dayDisabledBypassUntil = DateTime.Now.Add(bypassDuration);
                    }
                    else if (lockWin.WasMorningLockUnlock)
                    {
                        _morningPasswordBypassActive = true;
                        _isMorningLockMode = false;
                    }
                    else if (IsLunchRestrictedWindow(DateTime.Now))
                    {
                        var schedule = _configManager.GetScheduleForToday();
                        if (_lunchAccumulatedSeconds >= schedule.LunchMaxUsageMinutes * 60)
                        {
                            _lunchPasswordBypassActive = true;
                        }
                    }

                    StartUsageMode(lockWin.TemporaryUsageMinutes);
                    this.Show();
                }
            }
            else if (!_suppressLockReopen)
            {
                this.Dispatcher.BeginInvoke(new Action(() =>
                {
                    System.Threading.Thread.Sleep(500);
                    OpenLockWindow(isShutdownMode, isPasswordRequiredOnly, isPauseMode, requirePasswordForResume, isAppViolationPause, isMorningLockMode, isDayDisabledLock);
                }));
            }

            _suppressLockReopen = false;
        }

        private void AbortActiveLockWindowForRemoteCommand()
        {
            if (_activeLockWindow == null)
                return;

            _suppressLockReopen = true;
            _activeLockWindow.ForceAbortForRemoteCommand();
        }

        /// <summary>
        /// 远程锁定：锁定时长（分钟）
        /// </summary>
        public void RemoteLock(int minutes)
        {
            if (minutes <= 0) minutes = 30;

            // 服务端命令无条件生效：锁定立即取消未到期的远程解锁授权（最后下发的命令优先）
            CancelRemoteUnlockOverride();

            if (_activeLockWindow != null)
            {
                AbortActiveLockWindowForRemoteCommand();
            }

            _isTimingPaused = false;
            _isAppViolationPause = false;
            _pausedRemainingSeconds = 0;
            _isShutdownMode = false;
            _isPasswordRequiredOnly = false;
            _isResting = false;

            StartRestMode(DateTime.Now.AddMinutes(minutes));
        }

        /// <summary>
        /// 远程解锁：解锁后可使用时长（分钟）
        /// </summary>
        public void RemoteUnlock(int minutes)
        {
            if (minutes <= 0) minutes = 30;

            var unlockUntil = DateTime.Now.AddMinutes(minutes);
            SetRemoteUnlockOverride(unlockUntil);
            _isMorningLockMode = false;

            if (_activeLockWindow != null)
            {
                _suppressLockReopen = true;
                _activeLockWindow.ForceRemoteUnlock(minutes);
                return;
            }

            _isTimingPaused = false;
            _isAppViolationPause = false;
            _pausedRemainingSeconds = 0;
            _isResting = false;
            _isShutdownMode = false;
            _isPasswordRequiredOnly = false;
            StartUsageMode(minutes);
            this.Show();
        }

        private void SaveState()
        {
            try
            {
                // 格式：IsResting|TargetTime|IsShutdownMode|StatsDate|LunchSec|LunchBreaks|EveningSec|IsPasswordRequiredOnly|IsTimingPaused|PausedRemainingSec|IsAppViolationPause|TotalDailyUsageSec|EveningBypass|LunchBypass|MorningBypass|NightShutdownBypassUntil|MorningLockBypassUntil|LockedSecToday|IntegrityAnchor|CountedSecAtAnchor|SleepGraceSec|IntegrityLockActive|LastSaveTime
                File.WriteAllText(
                    _configPath,
                    $"{_isResting}|{_targetTime:o}|{_isShutdownMode}|{_usageStatsDate:yyyy-MM-dd}|{_lunchAccumulatedSeconds}|{_lunchBreaksTaken}|{_eveningAccumulatedSeconds}|{_isPasswordRequiredOnly}|{_isTimingPaused}|{_pausedRemainingSeconds}|{_isAppViolationPause}|{_totalDailyUsageSeconds}|{_eveningPasswordBypassActive}|{_lunchPasswordBypassActive}|{_morningPasswordBypassActive}|{_nightShutdownBypassUntil:o}|{_morningLockBypassUntil:o}|{_lockedSecondsToday}|{_integrityAnchor:o}|{_countedSecondsAtAnchor}|{_sleepGraceSeconds}|{_integrityLockActive}|{DateTime.Now:o}");
            }
            catch { }
        }

        private void LoadState()
        {
            bool loaded = false;
            var now = DateTime.Now;

            // 先恢复密码绕过状态，使夜间/早晨锁判断能正确尊重已解锁的绕过
            LoadBypassState();

            bool savedIntegrityLock = false;
            if (File.Exists(_configPath))
            {
                try
                {
                    var data = File.ReadAllText(_configPath).Split('|');
                    if (data.Length >= 18) double.TryParse(data[17], out _lockedSecondsToday);
                    if (data.Length >= 19 && DateTime.TryParse(data[18], out var anchor)) _integrityAnchor = anchor;
                    if (data.Length >= 20) double.TryParse(data[19], out _countedSecondsAtAnchor);
                    if (data.Length >= 21) double.TryParse(data[20], out _sleepGraceSeconds);
                    if (data.Length >= 22) bool.TryParse(data[21], out savedIntegrityLock);
                }
                catch { }
            }

            // 开机启动时在夜间关机时段自动进入锁屏
            if (IsNightlyShutdownTime(now) && now > _nightShutdownBypassUntil)
            {
                StartRestMode(null, true);
                return;
            }

            // 开机启动时在早晨锁定时段自动进入锁屏
            if (IsMorningLockTime(now) && now > _morningLockBypassUntil)
            {
                StartMorningLock(now);
                return;
            }

            // 升级/重启后完整性锁必须立即恢复，否则等于绕过时间记录异常
            // [临时] 暂时去掉“时间比对不符合”的提示：重启/升级后不再恢复完整性锁。
            // 需要恢复时，取消下面代码块的注释即可。
            //if (savedIntegrityLock)
            //{
            //    _integrityLockActive = true;
            //    StartRestMode(null, false, true, isIntegrityLock: true);
            //    return;
            //}

            // 注意：午间时段不直接锁屏，由定时器后续评估累计使用时间

            if (File.Exists(_configPath))
            {
                try
                {
                    var data = File.ReadAllText(_configPath).Split('|');
                    bool savedIsResting = bool.Parse(data[0]);
                    DateTime savedTargetTime = DateTime.Parse(data[1]);
                    bool savedIsShutdown = false;
                    bool savedIsPasswordRequiredOnly = false;
                    // 最后落盘时刻：用于计算程序不运行（卡死/关机/重启）期间的流逝时间
                    DateTime lastSaveTime = DateTime.MinValue;
                    if (data.Length >= 23 && DateTime.TryParse(data[22], out var parsedLastSave))
                        lastSaveTime = parsedLastSave;
                    if (lastSaveTime == DateTime.MinValue)
                    {
                        try { lastSaveTime = File.GetLastWriteTime(_configPath); } catch { }
                    }
                    if (data.Length >= 3) bool.TryParse(data[2], out savedIsShutdown);
                    if (data.Length >= 4 && DateTime.TryParse(data[3], out var statsDate)) _usageStatsDate = statsDate.Date;
                    if (data.Length >= 5) double.TryParse(data[4], out _lunchAccumulatedSeconds);
                    if (data.Length >= 6) int.TryParse(data[5], out _lunchBreaksTaken);
                    if (data.Length >= 7) double.TryParse(data[6], out _eveningAccumulatedSeconds);
                    if (data.Length >= 8) bool.TryParse(data[7], out savedIsPasswordRequiredOnly);
                    bool savedIsTimingPaused = false;
                    if (data.Length >= 9) bool.TryParse(data[8], out savedIsTimingPaused);
                    if (data.Length >= 10) double.TryParse(data[9], out _pausedRemainingSeconds);
                    if (data.Length >= 11) bool.TryParse(data[10], out _isAppViolationPause);
                    if (data.Length >= 12) double.TryParse(data[11], out _totalDailyUsageSeconds);
                    if (data.Length >= 13) bool.TryParse(data[12], out _eveningPasswordBypassActive);
                    if (data.Length >= 14) bool.TryParse(data[13], out _lunchPasswordBypassActive);
                    if (data.Length >= 15) bool.TryParse(data[14], out _morningPasswordBypassActive);
                    if (data.Length >= 16 && DateTime.TryParse(data[15], out var nightBypass)) _nightShutdownBypassUntil = nightBypass;
                    if (data.Length >= 17 && DateTime.TryParse(data[16], out var morningBypass)) _morningLockBypassUntil = morningBypass;

                    if (savedIsTimingPaused)
                    {
                        _isTimingPaused = true;
                        OpenLockWindow(false, false, true, isAppViolationPause: _isAppViolationPause);
                        loaded = true;
                    }
                    else if (savedIsShutdown)
                    {
                        // 重启后不恢复夜间关机锁（状态已过期）
                        StartUsageMode();
                    }
                    else if (savedIsResting)
                    {
                        if (savedIsPasswordRequiredOnly)
                        {
                            if (IsEveningRestrictedWindow(now) && !IsLunchRestrictedWindow(now)
                                && now < savedTargetTime)
                            {
                                StartRestMode(null, false, true);
                            }
                            else
                            {
                                // 重启后状态过期（早上了），不恢复锁定
                                StartUsageMode();
                            }
                        }
                        else if ((savedTargetTime - now).TotalHours > 24)
                        {
                            StartUsageMode();
                        }
                        else if (now < savedTargetTime)
                        {
                            StartRestMode(savedTargetTime, false);
                        }
                        else
                        {
                            // 重启后休息时间已过，不恢复锁定
                            StartUsageMode();
                        }
                    }
                    else
                    {
                        if (now < savedTargetTime)
                        {
                            _isResting = false;
                            _isShutdownMode = false;
                            _targetTime = savedTargetTime;
                        }
                        else
                        {
                            // 修复：卡顿/关机重启后，使用阶段目标时间在“程序不运行期间”已过时，
                            // 不能直接重新计时（否则重启即可绕过锁定，丢失锁定时间）。
                            // 程序不运行期间的流逝时间继续计入使用阶段，耗尽后再计入休息阶段：
                            // - 整个“剩余使用 + 休息时段”都在不运行期间耗完 → 视为休息完成，发放新使用时段；
                            // - 否则接续剩余休息时间（锁屏），锁定时间不丢失。
                            var gapSeconds = (now - lastSaveTime).TotalSeconds;
                            var remainingAtSave = Math.Max(0, (savedTargetTime - lastSaveTime).TotalSeconds);

                            int usage, rest;
                            GetDurationsForNow(out usage, out rest);

                            if (lastSaveTime == DateTime.MinValue || gapSeconds < 0)
                            {
                                // 保存时间无效或时钟异常：保守接续为完整休息，避免直接发放使用时间
                                Debug.WriteLine($"重启接续：状态保存时间无效，进入完整休息");
                                StartRestMode();
                            }
                            else if (lastSaveTime.Date != now.Date)
                            {
                                // 跨天：夜间/早晨锁已在前面处理，新的一天重新开始计时
                                Debug.WriteLine($"重启接续：已跨天，开始新一天的计时");
                                StartUsageMode();
                            }
                            else if (gapSeconds - remainingAtSave >= rest * 60)
                            {
                                // 不运行期间剩余使用与整个休息时段均已流逝 → 休息视为完成
                                Debug.WriteLine($"重启接续：关机期间已完成休息，开始新使用时段");
                                StartUsageMode();
                            }
                            else
                            {
                                // 使用阶段已在不运行期间耗尽，锁定剩余休息时间
                                var overtime = Math.Max(0, gapSeconds - remainingAtSave);
                                var restRemaining = Math.Max(60, rest * 60 - overtime);
                                Debug.WriteLine($"重启接续：使用阶段已在关机期间结束，锁定剩余休息 {restRemaining / 60.0:F1} 分钟");
                                StartRestMode(now.AddSeconds(restRemaining));
                            }
                        }
                    }

                    loaded = true;
                }
                catch
                {
                    // 文件解析出错
                }
            }

            // 新安装或配置文件丢失
            if (!loaded)
            {
                StartUsageMode();
            }

            ResetDailyUsageIfNeeded(now);
            _lastLogicTick = DateTime.Now;
        }

        /// <summary>
        /// 从 state.txt 预读密码绕过状态（不恢复完整运行状态），
        /// 使 LoadState 中的夜间/早晨锁判断能正确尊重已解锁的绕过。
        /// </summary>
        private void LoadBypassState()
        {
            if (!File.Exists(_configPath))
                return;

            try
            {
                var data = File.ReadAllText(_configPath).Split('|');
                if (data.Length >= 13) bool.TryParse(data[12], out _eveningPasswordBypassActive);
                if (data.Length >= 14) bool.TryParse(data[13], out _lunchPasswordBypassActive);
                if (data.Length >= 15) bool.TryParse(data[14], out _morningPasswordBypassActive);
                if (data.Length >= 16 && DateTime.TryParse(data[15], out var nightBypass)) _nightShutdownBypassUntil = nightBypass;
                if (data.Length >= 17 && DateTime.TryParse(data[16], out var morningBypass)) _morningLockBypassUntil = morningBypass;
            }
            catch { }
        }

        private void GetDurationsForNow(out int usageMinutes, out int restMinutes)
        {
            var schedule = _configManager.GetScheduleForToday();
            usageMinutes = schedule.UsageMinutes;
            restMinutes = schedule.RestMinutes;
        }

        private bool IsLunchRestrictedWindow(DateTime now)
        {
            var schedule = _configManager.GetScheduleForToday();
            if (!schedule.LunchRestrictionEnabled) return false;

            var t = now.TimeOfDay;
            return t >= schedule.GetLunchStartTime() &&
                   t < schedule.GetLunchEndTime();
        }

        private bool IsEveningRestrictedWindow(DateTime now)
        {
            var schedule = _configManager.GetScheduleForToday();
            if (!schedule.EveningRestrictionEnabled) return false;

            var t = now.TimeOfDay;
            return t >= schedule.GetEveningStartTime() &&
                   t < schedule.GetEveningEndTime();
        }

        private DateTime GetLunchWindowEnd(DateTime now)
        {
            var schedule = _configManager.GetScheduleForToday();
            return now.Date + schedule.GetLunchEndTime();
        }

        private double GetRemainingLunchUsageSeconds(DateTime now)
        {
            var schedule = _configManager.GetScheduleForToday();
            if (!schedule.LunchRestrictionEnabled || schedule.LunchMaxUsageMinutes <= 0)
                return double.MaxValue;

            return Math.Max(0, schedule.LunchMaxUsageMinutes * 60 - _lunchAccumulatedSeconds);
        }

        /// <summary>
        /// 午间仅限制累计使用上限，不替换正常使用/休息周期；必要时缩短当前阶段剩余时间。
        /// </summary>
        private void CapTargetTimeToLunchAllowance(DateTime now)
        {
            var schedule = _configManager.GetScheduleForToday();
            if (!schedule.LunchRestrictionEnabled || !IsLunchRestrictedWindow(now))
                return;

            var lunchRemaining = GetRemainingLunchUsageSeconds(now);
            if (lunchRemaining <= 0 || lunchRemaining >= double.MaxValue)
                return;

            var maxTarget = now.AddSeconds(lunchRemaining);
            if (_targetTime > maxTarget)
            {
                _targetTime = maxTarget;
                SaveState();
            }
        }

        private bool IsNightlyShutdownTime(DateTime now)
        {
            var schedule = _configManager.GetScheduleForToday();
            return now.TimeOfDay >= schedule.GetNightShutdownTime();
        }

        private bool IsMorningLockTime(DateTime now)
        {
            var schedule = _configManager.GetScheduleForToday();
            if (!schedule.MorningLockEnabled)
                return false;

            return now.TimeOfDay < schedule.GetMorningUnlockTime();
        }

        private void ResetDailyUsageIfNeeded(DateTime now)
        {
            if (_usageStatsDate.Date == now.Date)
            {
                return;
            }

            _usageStatsDate = now.Date;
            _totalDailyUsageSeconds = 0;
            _lockedSecondsToday = 0;
            _lunchAccumulatedSeconds = 0;
            _lunchBreaksTaken = 0;
            _eveningAccumulatedSeconds = 0;
            _eveningPasswordBypassActive = false;
            _lunchPasswordBypassActive = false;
            _morningPasswordBypassActive = false;

            // 完整性锁不因跨天自动解锁（由密码解锁后重新锚定）
            if (_integrityLockActive)
            {
                SaveState();
                return;
            }

            if (_isPasswordRequiredOnly && !IsNightlyShutdownTime(now))
            {
                _isPasswordRequiredOnly = false;
                _isResting = false;
                StartUsageMode();
            }

            SaveState();
        }

        private void AccumulateUsage(DateTime now)
        {
            var delta = (now - _lastLogicTick).TotalSeconds;
            _lastLogicTick = now;

            if (delta <= 0)
            {
                return;
            }

            // 睡眠/休眠/长时间停顿（>90秒）：不累计开屏也不累计锁屏，记入豁免，
            // 避免把睡眠时间当成“时间记录缺失”触发完整性锁屏。
            if (delta > 90)
            {
                _sleepGraceSeconds += delta;
                return;
            }

            // 锁屏状态：休息 / 夜间关机 / 暂停 / 早晨锁定（早晨锁和违规暂停已置 _isResting/_isTimingPaused）
            bool locked = _isResting || _isShutdownMode || _isTimingPaused;

            if (locked)
            {
                _lockedSecondsToday += delta;
            }
            else
            {
                // 当日总使用时长（开屏时间）
                _totalDailyUsageSeconds += delta;

                if (IsLunchRestrictedWindow(now))
                {
                    _lunchAccumulatedSeconds += delta;
                }

                if (IsEveningRestrictedWindow(now) && !IsLunchRestrictedWindow(now))
                {
                    _eveningAccumulatedSeconds += delta;
                }
            }
        }

        private void InitializeIntegrityAnchor(DateTime now)
        {
            // 完整性锚点：从每天开机时刻（或零点、完整性锁解锁后）重新起算
            if (_integrityAnchor != DateTime.MinValue && _integrityAnchor.Date == now.Date)
            {
                // 锚点早于系统本次启动时刻，说明锚点属于上一次开机会话：
                // 期间的缺口是正常关机断电（如当天长时间关机后再开机），不属于绕过计时，
                // 重新锚定以免误报“时间记录异常”。同一次开机会话内强杀进程仍会被检出。
                var sessionStart = GetSystemSessionStart();
                if (sessionStart.HasValue && _integrityAnchor < sessionStart.Value)
                    ReanchorIntegrity();

                return;
            }

            ReanchorIntegrity();
        }

        /// <summary>
        /// 系统本次会话（最近一次开机或快速启动恢复）的开始时刻，取两者较晚者：
        /// 1) TickCount64 推算的内核启动时刻（普通重启会刷新）；
        /// 2) 系统事件日志 6005（事件日志服务启动）的时间（快速启动的关机-开机循环后也会刷新）。
        /// </summary>
        private DateTime? GetSystemSessionStart()
        {
            if (_systemSessionStartResolved)
                return _systemSessionStart;

            _systemSessionStartResolved = true;

            try
            {
                _systemSessionStart = DateTime.Now - TimeSpan.FromMilliseconds(GetTickCount64());
            }
            catch { }

            try
            {
                var query = new EventLogQuery("System", PathType.LogName, "*[System[EventID=6005]]")
                {
                    ReverseDirection = true
                };
                using var reader = new EventLogReader(query);
                var lastLogStart = reader.ReadEvent()?.TimeCreated;
                if (lastLogStart.HasValue &&
                    (_systemSessionStart == null || lastLogStart.Value > _systemSessionStart.Value))
                {
                    _systemSessionStart = lastLogStart;
                }
            }
            catch
            {
                // 事件日志不可读时仅用 TickCount 推算值
            }

            return _systemSessionStart;
        }

        /// <summary>
        /// 重新锚定：记录当前时刻与已累计(开屏+锁屏)秒数。
        /// 锚定后“开屏+锁屏”与本机时钟同步推进，若有人改系统时间或
        /// 关机时段被挪用（强杀、休眠绕过、重装等），累计会少于时钟流逝而触发完整性锁。
        /// </summary>
        private void ReanchorIntegrity()
        {
            _integrityAnchor = DateTime.Now;
            _countedSecondsAtAnchor = _totalDailyUsageSeconds + _lockedSecondsToday;
            _sleepGraceSeconds = 0;
        }

        /// <summary>
        /// 完整性校验：自锚点起的时钟流逝 与 (开屏+锁屏+睡眠豁免) 是否吻合。
        /// 差值超过 10 分钟即视为时间记录异常（被绕过/时钟被改）。
        /// </summary>
        private bool IntegrityMismatch(DateTime now)
        {
            if (_integrityAnchor == DateTime.MinValue || _integrityAnchor.Date != now.Date)
                return false;

            var elapsed = (now - _integrityAnchor).TotalSeconds;
            if (elapsed <= IntegrityMismatchThresholdSeconds)
                return false;

            var counted = _totalDailyUsageSeconds + _lockedSecondsToday - _countedSecondsAtAnchor + _sleepGraceSeconds;
            var gap = elapsed - counted;
            return gap > IntegrityMismatchThresholdSeconds;
        }

        private void ClearEveningLockDuringLunch(DateTime now)
        {
            if (!IsLunchRestrictedWindow(now) || !_isPasswordRequiredOnly)
                return;

            _isPasswordRequiredOnly = false;
            _eveningPasswordBypassActive = false;

            if (_isResting && _activeLockWindow != null)
            {
                AbortActiveLockWindowForRemoteCommand();
                _isResting = false;
                StartUsageMode();
            }
        }

        private void EvaluateTimeWindowRules(DateTime now)
        {
            ClearEveningLockDuringLunch(now);

            if (_isResting || _isShutdownMode || _isTimingPaused)
            {
                return;
            }

            var schedule = _configManager.GetScheduleForToday();

            if (IsLunchRestrictedWindow(now))
            {
                if (!_lunchPasswordBypassActive &&
                    schedule.LunchMaxUsageMinutes > 0 &&
                    _lunchAccumulatedSeconds >= schedule.LunchMaxUsageMinutes * 60)
                {
                    StartRestMode(GetLunchWindowEnd(now), false, false);
                    return;
                }

                if (!_isResting && !_isShutdownMode && !_isTimingPaused)
                    CapTargetTimeToLunchAllowance(now);

                // 午间时段不评估晚间规则
                return;
            }

            if (IsEveningRestrictedWindow(now) &&
                _eveningAccumulatedSeconds >= schedule.EveningMaxUsageMinutes * 60)
            {
                if (_eveningPasswordBypassActive)
                {
                    return;
                }

                StartRestMode(null, false, true);
            }
            else if (_isPasswordRequiredOnly && !_isResting && !IsEveningRestrictedWindow(now))
            {
                _isPasswordRequiredOnly = false;
            }
        }

        #region 托盘与系统设置 (保持原样或微调)

        private void InitNotifyIcon()
        {
            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("显示主界面", null, (s, e) =>
            {
                this.Show();
                this.WindowState = WindowState.Normal;
                this.Activate();
            });
            contextMenu.Items.Add("退出程序", null, (s, e) =>
            {
                var authWin = new LockWindow(DateTime.Now, _adminPass, true);
                if (authWin.ShowDialog() == true)
                {
                    _notifyIcon.Dispose();
                    System.Windows.Application.Current.Shutdown();
                }
            });

            _notifyIcon = new NotifyIcon
            {
                Text = "家长控制服务",
                Icon = System.Drawing.SystemIcons.Shield, // 确保引用 System.Drawing
                Visible = true,
                ContextMenuStrip = contextMenu
            };

            _notifyIcon.DoubleClick += (s, e) =>
            {
                this.Show();
                this.WindowState = WindowState.Normal;
            };
        }

        private void SetAutoStart()
        {
            try
            {
                string path = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                key.SetValue("ControlTimeService", $"\"{path}\"");
            }
            catch { }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            this.Hide();
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            // 进程退出前再保存一次，避免升级/异常退出丢失锁定状态与剩余时间
            SaveState();
            CrashLogger.Log("进程 OnClosed 退出");
            _statusTimer?.Stop();
            _watchdogTimer?.Stop();
            _autoUpdater?.Stop();
            _notifyIcon.Dispose();
            base.OnClosed(e);
        }

        #endregion

        /// <summary>
        /// 远程暂停计时（锁屏并冻结剩余时间）
        /// </summary>
        public void RemotePauseTiming()
        {
            if (_isTimingPaused || _isShutdownMode)
                return;

            // 服务端命令无条件生效：暂停同样取消未到期的远程解锁授权
            CancelRemoteUnlockOverride();

            if (_activeLockWindow != null)
            {
                AbortActiveLockWindowForRemoteCommand();
            }

            _isResting = false;
            _isPasswordRequiredOnly = false;
            StartPauseMode();
        }

        public void ApplyAppPolicy(AppPolicy policy)
        {
            _appPolicyManager.UpdateFromRemote(policy);
            _appMonitor.SetPolicy(_appPolicyManager.GetPolicy());
        }

        public void ApplyRemoteConfig(Dictionary<string, DaySchedule> remoteConfig)
        {
            _configManager.UpdateFromRemote(remoteConfig);
            ApplyConfigChanges();
            ApplyUsageLimitFromConfig();

            if (IsRemoteUnlockActive(DateTime.Now))
            {
                ShowNotification("设置已更新", "远程解锁期间暂不改变当前锁屏状态");
                return;
            }

            var schedule = _configManager.GetScheduleForToday();
            if (!schedule.Enabled)
            {
                if (!_isResting && !_isShutdownMode)
                    StartRestMode(null, false, true, isDayDisabledLock: true);
            }
            else if (TryExitDayDisabledPasswordLock())
            {
                // 管理端重新启用当天控制：关闭锁屏并进入使用
            }

            ShowNotification("设置已更新", "管理端下发的配置已生效");
        }

        /// <summary>
        /// 当天从「未启用」密码锁恢复为可用时：必须关掉当前锁屏窗口，否则配置已生效但屏幕仍锁着。
        /// </summary>
        private bool TryExitDayDisabledPasswordLock()
        {
            var now = DateTime.Now;
            if (!_isPasswordRequiredOnly)
                return false;
            if (IsEveningRestrictedWindow(now) || IsLunchRestrictedWindow(now))
                return false;

            var usageMinutes = Math.Max(1, _configManager.GetScheduleForToday().UsageMinutes);

            if (_activeLockWindow != null)
            {
                _suppressLockReopen = true;
                _isPasswordRequiredOnly = false;
                _isResting = false;
                _isShutdownMode = false;
                _activeLockWindow.ForceRemoteUnlock(usageMinutes);
                return true;
            }

            _isPasswordRequiredOnly = false;
            _isResting = false;
            StartUsageMode(usageMinutes);
            this.Show();
            return true;
        }

        private void ApplyUsageLimitFromConfig()
        {
            var schedule = _configManager.GetScheduleForToday();
            if (!schedule.Enabled || IsRemoteUnlockActive(DateTime.Now))
                return;

            if (_isResting || _isShutdownMode || _isTimingPaused || _isMorningLockMode)
                return;

            var maxSeconds = schedule.UsageMinutes * 60.0;
            var remaining = (_targetTime - DateTime.Now).TotalSeconds;
            if (remaining <= 0)
                return;

            if (Math.Abs(remaining - maxSeconds) > 1)
            {
                _targetTime = DateTime.Now.AddSeconds(maxSeconds);
                CapTargetTimeToLunchAllowance(DateTime.Now);
                SaveState();
            }
        }

        public void ApplyConfigChanges()
        {
            _appMonitor.SetPolicy(_appPolicyManager.GetPolicy());

            var now = DateTime.Now;
            if (IsRemoteUnlockActive(now))
            {
                ReportStatusToControl();
                return;
            }

            var schedule = _configManager.GetScheduleForToday();

            // 立即应用“启用此天控制”开关，避免保存后仍按旧状态运行。
            if (!schedule.Enabled)
            {
                if (!_isResting && !_isShutdownMode && !_isTimingPaused)
                    StartRestMode(null, false, true, isDayDisabledLock: true);

                ReportStatusToControl();
                return;
            }

            if (TryExitDayDisabledPasswordLock())
            {
                ReportStatusToControl();
                return;
            }

            ClearEveningLockDuringLunch(now);
            EvaluateTimeWindowRules(now);
            ReportStatusToControl();
        }

        private double GetReportableRemainingSeconds()
        {
            if (_isShutdownMode)
                return 0;

            if (_isTimingPaused)
                return Math.Max(0, _pausedRemainingSeconds);

            if (_isResting)
                return Math.Max(0, (_targetTime - DateTime.Now).TotalSeconds);

            var remaining = (_targetTime - DateTime.Now).TotalSeconds;
            if (remaining < 0)
                return 0;
            if (remaining > 86400)
                return 0;

            return remaining;
        }

        private bool IsClientLockedForUpdate()
        {
            return _isResting || _isShutdownMode || _isTimingPaused || _activeLockWindow != null;
        }

        private void TryApplyPendingUpdate()
        {
            if (string.IsNullOrWhiteSpace(_pendingUpdateUrl))
                return;

            if (!IsClientLockedForUpdate())
                return;

            var packageUrl = _pendingUpdateUrl;
            var version = _pendingUpdateVersion;
            _pendingUpdateUrl = null;
            _pendingUpdateVersion = null;

            // 升级重启前落盘，保留当前锁定/解锁与剩余时间
            SaveState();
            _ = _autoUpdater.ApplyUpdateAsync(packageUrl, version, force: true);
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            StartPauseMode();
        }

        /// <summary>
        /// 远程推送升级（锁屏时自动安装，否则排队等待）
        /// </summary>
        public void ShowNotification(string title, string message)
        {
            _notifyIcon?.ShowBalloonTip(5000, title, message, ToolTipIcon.Info);
        }

        /// <summary>
        /// 显示消息对话框（含回复功能）
        /// </summary>
        public void ShowMessageDialog(string title, string message)
        {
            // 先弹通知
            _notifyIcon?.ShowBalloonTip(5000, title, message, ToolTipIcon.Info);

            // 如果窗口已隐藏或没有控制端连接，不显示对话框
            if (_controlClient == null) return;

            // 在 UI 线程创建对话框
            var win = new Window
            {
                Title = title,
                Width = 420,
                Height = 250,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = true,
                Content = new System.Windows.Controls.Grid { Margin = new Thickness(15) }
            };
            var grid = (System.Windows.Controls.Grid)win.Content;
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });

            // 消息内容
            var msgBlock = new System.Windows.Controls.TextBlock
            {
                Text = message,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
                FontSize = 14,
                Foreground = System.Windows.Media.Brushes.Black
            };
            System.Windows.Controls.Grid.SetRow(msgBlock, 0);
            grid.Children.Add(msgBlock);

            // 回复输入框
            var replyBox = new System.Windows.Controls.TextBox
            {
                AcceptsReturn = true,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 8)
            };
            System.Windows.Controls.Grid.SetRow(replyBox, 1);
            grid.Children.Add(replyBox);

            // 按钮
            var btnPanel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            var sendBtn = new System.Windows.Controls.Button { Content = "发送回复", Width = 100, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            var closeBtn = new System.Windows.Controls.Button { Content = "关闭", Width = 80, Height = 30 };
            btnPanel.Children.Add(sendBtn);
            btnPanel.Children.Add(closeBtn);
            System.Windows.Controls.Grid.SetRow(btnPanel, 2);
            grid.Children.Add(btnPanel);

            sendBtn.Click += async (s, args) =>
            {
                var reply = replyBox.Text.Trim();
                if (!string.IsNullOrEmpty(reply) && await _controlClient.SendMessageToServerAsync(reply))
                {
                    _notifyIcon?.ShowBalloonTip(2000, "回复已发送", $"回复内容: {reply}", ToolTipIcon.Info);
                    replyBox.Text = "";
                }
            };
            closeBtn.Click += (s, args) => win.Close();

            // Ctrl+Enter 发送
            replyBox.KeyDown += async (s, args) =>
            {
                if (args.Key == System.Windows.Input.Key.Enter && args.KeyboardDevice.Modifiers == System.Windows.Input.ModifierKeys.Control)
                {
                    var reply = replyBox.Text.Trim();
                    if (!string.IsNullOrEmpty(reply) && await _controlClient.SendMessageToServerAsync(reply))
                    {
                        _notifyIcon?.ShowBalloonTip(2000, "回复已发送", $"回复内容: {reply}", ToolTipIcon.Info);
                        replyBox.Text = "";
                    }
                }
            };

            win.Show();
        }

        public void TriggerRemoteUpdate(string packageUrl, string version)
        {
            packageUrl = AutoUpdater.NormalizeDownloadUrl(packageUrl);

            // 升级重启前落盘，确保锁定/解锁状态与剩余时间不因重启丢失
            SaveState();

            // 立即安装，避免「使用中/远程解锁」时升级一直排队、问题修不好
            _pendingUpdateUrl = null;
            _pendingUpdateVersion = null;
            _notifyIcon?.ShowBalloonTip(
                3000,
                "ControlTimeService 升级",
                $"正在安装版本 {version ?? ""}...",
                ToolTipIcon.Info);
            _ = _autoUpdater.ApplyUpdateAsync(packageUrl, version, force: true);
        }

        private void ConfigButton_Click(object sender, RoutedEventArgs e)
        {
            var authWin = new LockWindow(DateTime.Now, _adminPass, true);
            if (authWin.ShowDialog() != true)
                return;

            var configWindow = new ConfigWindow(_configManager, _appPolicyManager);
            configWindow.Owner = this;
            if (configWindow.ShowDialog() == true)
            {
                ApplyConfigChanges();
                ApplyUsageLimitFromConfig();
                ShowNotification("配置已保存", "时间与应用权限设置已生效");
            }
        }

        private void MessagesButton_Click(object sender, RoutedEventArgs e)
        {
            if (_controlClient == null)
            {
                ShowNotification("无法打开消息", "未连接到管理端");
                return;
            }

            OpenMessagesWindow();
        }

        private void OpenMessagesWindow()
        {
            var msgWin = new Window
            {
                Title = "消息列表",
                Width = 460,
                Height = 420,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Topmost = true,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                Content = new System.Windows.Controls.Grid { Margin = new Thickness(15) }
            };
            var grid = (System.Windows.Controls.Grid)msgWin.Content;
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });

            var historyBox = new System.Windows.Controls.TextBox
            {
                IsReadOnly = true,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 10),
                FontSize = 13
            };
            System.Windows.Controls.Grid.SetRow(historyBox, 0);
            grid.Children.Add(historyBox);

            var replyBox = new System.Windows.Controls.TextBox
            {
                AcceptsReturn = true,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Height = 60,
                Margin = new Thickness(0, 0, 0, 8)
            };
            System.Windows.Controls.Grid.SetRow(replyBox, 1);
            grid.Children.Add(replyBox);

            var btnPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            var refreshBtn = new System.Windows.Controls.Button { Content = "刷新", Width = 80, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            var sendBtn = new System.Windows.Controls.Button { Content = "发送回复", Width = 100, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            var closeBtn = new System.Windows.Controls.Button { Content = "关闭", Width = 80, Height = 30 };
            btnPanel.Children.Add(refreshBtn);
            btnPanel.Children.Add(sendBtn);
            btnPanel.Children.Add(closeBtn);
            System.Windows.Controls.Grid.SetRow(btnPanel, 2);
            grid.Children.Add(btnPanel);

            async Task LoadHistoryAsync()
            {
                var msgs = await _controlClient.FetchMessagesAsync();
                if (msgs.Count == 0)
                {
                    historyBox.Text = "暂无消息记录";
                    return;
                }

                var lines = new List<string>();
                foreach (var m in msgs)
                {
                    var dir = m.Direction == "from_client" ? "我" : "管理端";
                    lines.Add($"[{m.Timestamp:MM-dd HH:mm:ss}] {dir}：{m.Text}");
                }
                historyBox.Text = string.Join(Environment.NewLine, lines);
                historyBox.ScrollToEnd();
            }

            refreshBtn.Click += async (s, args) => await LoadHistoryAsync();
            sendBtn.Click += async (s, args) =>
            {
                var reply = replyBox.Text.Trim();
                if (string.IsNullOrEmpty(reply))
                    return;

                if (!await _controlClient.SendMessageToServerAsync(reply))
                    return;

                replyBox.Text = "";
                ShowNotification("回复已发送", reply);
                await LoadHistoryAsync();
            };
            closeBtn.Click += (s, args) => msgWin.Close();
            replyBox.KeyDown += async (s, args) =>
            {
                if (args.Key == System.Windows.Input.Key.Enter &&
                    args.KeyboardDevice.Modifiers == System.Windows.Input.ModifierKeys.Control)
                {
                    var reply = replyBox.Text.Trim();
                    if (!string.IsNullOrEmpty(reply))
                    {
                        if (!await _controlClient.SendMessageToServerAsync(reply))
                            return;

                        replyBox.Text = "";
                        await LoadHistoryAsync();
                    }
                }
            };

            var refreshTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            refreshTimer.Tick += async (s, args) => await LoadHistoryAsync();
            refreshTimer.Start();
            msgWin.Closed += (s, args) => refreshTimer.Stop();

            _ = LoadHistoryAsync();
            msgWin.Show();
        }

        private string LoadControlServerUrl()
        {
            return ControlConfig.Load().ServerUrl;
        }

        private DateTime _lastViolationPushTime = DateTime.MinValue;
        private string _lastViolationPushText = string.Empty;

        /// <summary>
        /// 违规/预警推送到控制端消息记录（fire-and-forget，失败仅写调试日志）。
        /// 相同内容 30 秒内去重，避免杀进程失败重试时刷屏。
        /// </summary>
        private void PushViolationToServer(string text)
        {
            try
            {
                if (_controlClient == null || string.IsNullOrWhiteSpace(text))
                    return;

                var now = DateTime.Now;
                if (string.Equals(text, _lastViolationPushText, StringComparison.Ordinal) &&
                    (now - _lastViolationPushTime).TotalSeconds < 30)
                    return;

                _lastViolationPushText = text;
                _lastViolationPushTime = now;

                _ = _controlClient.SendMessageToServerAsync(text);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"违规推送失败: {ex.Message}");
            }
        }

        private void ReportStatusToControl()
        {
            if (_controlClient == null) return;

            var status = new
            {
                isResting = _isResting,
                isShutdownMode = _isShutdownMode,
                isTimingPaused = _isTimingPaused,
                remainingSeconds = GetReportableRemainingSeconds(),
                totalUsageSecondsToday = _totalDailyUsageSeconds,
                openSecondsToday = _totalDailyUsageSeconds,
                lockedSecondsToday = _lockedSecondsToday,
                lastBlockedApp = _appMonitor?.LastBlockedReason,
                config = _configManager.GetAllSchedules(),
                appPolicy = _appPolicyManager.GetPolicy()
            };

            _controlClient.UpdateStatus(status);
        }
    }
}
