using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Forms; // 需引用 System.Windows.Forms
using System.Windows.Threading;
using System.Diagnostics;
using System.Text.Json;

namespace ControlTimeService
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer _logicTimer = new DispatcherTimer();
        private System.Windows.Threading.DispatcherTimer _statusTimer;

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
        private bool _isAppViolationPause = false;
        private bool _isMorningLockMode = false;
        private bool _morningPasswordBypassActive = false;
        private DateTime _appBlockCooldownUntil = DateTime.MinValue;
        private DateTime _lastLogicTick = DateTime.Now;
        private readonly ConcurrentQueue<Action> _remoteCommandQueue = new();
        private bool _suppressLockReopen = false;
        private string _pendingUpdateUrl;
        private string _pendingUpdateVersion;

        private string _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "state.txt");
        private string _adminPass = "lnbxSoftLizhenNiping"; // 管理密码
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
            InitNotifyIcon();

            // 初始化配置管理器
            _configManager = new TimeConfigManager();
            _appPolicyManager = new AppPolicyManager(_configManager);

            // 初始化应用监控器
            _appMonitor = new AppMonitor();
            _appMonitor.SetPolicy(_appPolicyManager.GetPolicy());
            _appMonitor.OnAppBlocked += (s, e) =>
            {
                this.Dispatcher.Invoke(() =>
                {
                    if (DateTime.Now >= _appBlockCooldownUntil &&
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
                });
            };
            _appMonitor.OnDouyinGameVideoWarning += (s, e) =>
            {
                this.Dispatcher.Invoke(() =>
                {
                    _notifyIcon?.ShowBalloonTip(
                        3000,
                        "游戏视频提醒",
                        $"检测到游戏视频，{e.RemainingSeconds} 秒后将关闭应用",
                        ToolTipIcon.Warning);
                });
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
                _notifyIcon?.ShowBalloonTip(3000, "ControlTimeService 升级", message, ToolTipIcon.Info);
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

            // 启动时如果是静默启动可以 Hide，这里为了演示默认 Show，你可以改为 Hide
            // this.Hide(); 
        }

        public void EnqueueRemoteCommand(Action action)
        {
            if (action != null)
                _remoteCommandQueue.Enqueue(action);
        }

        public void ProcessRemoteCommandQueue()
        {
            while (_remoteCommandQueue.TryDequeue(out var action))
            {
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

        private void LogicTimer_Tick(object sender, EventArgs e)
        {
            ProcessRemoteCommandQueue();

            var now = DateTime.Now;
            ResetDailyUsageIfNeeded(now);
            AccumulateUsage(now);

            TryApplyPendingUpdate();

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
            if (!schedule.Enabled && !_isResting && !_isShutdownMode && !_isTimingPaused)
            {
                StartRestMode(null, false, true);
                return;
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

            _targetTime = DateTime.Now.AddMinutes(customUsageMinutes ?? usage);
            SaveState();
        }

        /// <summary>
        /// 开启强制休息模式（根据当天配置）
        /// </summary>
        private void StartRestMode(DateTime? customEndTime = null, bool isShutdownMode = false, bool isPasswordRequiredOnly = false)
        {
            _isResting = true;
            _isShutdownMode = isShutdownMode;
            _isPasswordRequiredOnly = isPasswordRequiredOnly;
            _isMorningLockMode = false;
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

            OpenLockWindow(_isShutdownMode, isPasswordRequiredOnly, false);
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
            if (_isResting || _isShutdownMode || _isTimingPaused || _activeLockWindow != null)
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
            bool isMorningLockMode = false)
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
                isMorningLockMode);

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
                        _eveningPasswordBypassActive = true;
                        _lunchPasswordBypassActive = true;
                        // 防止夜间关机时间和早晨锁定立刻重新锁定
                        var bypassDuration = TimeSpan.FromMinutes(lockWin.TemporaryUsageMinutes ?? 30);
                        _nightShutdownBypassUntil = DateTime.Now.Add(bypassDuration);
                        _morningLockBypassUntil = DateTime.Now.Add(bypassDuration);
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
                    OpenLockWindow(isShutdownMode, isPasswordRequiredOnly, isPauseMode, requirePasswordForResume, isAppViolationPause, isMorningLockMode);
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
            _eveningPasswordBypassActive = false;
            StartUsageMode(minutes);
            this.Show();
        }

        private void SaveState()
        {
            try
            {
                // 格式：IsResting|TargetTime|IsShutdownMode|StatsDate|LunchSec|LunchBreaks|EveningSec|IsPasswordRequiredOnly|IsTimingPaused|PausedRemainingSec|IsAppViolationPause
                File.WriteAllText(
                    _configPath,
                    $"{_isResting}|{_targetTime:o}|{_isShutdownMode}|{_usageStatsDate:yyyy-MM-dd}|{_lunchAccumulatedSeconds}|{_lunchBreaksTaken}|{_eveningAccumulatedSeconds}|{_isPasswordRequiredOnly}|{_isTimingPaused}|{_pausedRemainingSeconds}|{_isAppViolationPause}|{_totalDailyUsageSeconds}");
            }
            catch { }
        }

        private void LoadState()
        {
            bool loaded = false;
            var now = DateTime.Now;

            // 开机启动时在夜间关机时段自动进入锁屏
            if (IsNightlyShutdownTime(now))
            {
                StartRestMode(null, true);
                return;
            }

            // 开机启动时在早晨锁定时段自动进入锁屏
            if (IsMorningLockTime(now))
            {
                StartMorningLock(now);
                return;
            }

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
                            StartUsageMode();
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
            _lunchAccumulatedSeconds = 0;
            _lunchBreaksTaken = 0;
            _eveningAccumulatedSeconds = 0;
            _eveningPasswordBypassActive = false;
            _lunchPasswordBypassActive = false;
            _morningPasswordBypassActive = false;

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

            if (delta <= 0 || delta > 10)
            {
                return;
            }

            if (_isResting || _isShutdownMode || _isTimingPaused)
            {
                return;
            }

            // 累加当日总使用时长
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
                    _lunchAccumulatedSeconds >= schedule.LunchMaxUsageMinutes * 60)
                {
                    StartRestMode(GetLunchWindowEnd(now), false, false);
                    return;
                }

                if (_lunchBreaksTaken < 1 && _lunchAccumulatedSeconds >= 30 * 60)
                {
                    _lunchBreaksTaken++;
                    StartRestMode(now.AddMinutes(15), false, false);
                    return;
                }

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
            _statusTimer?.Stop();
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

            var schedule = _configManager.GetScheduleForToday();
            if (!schedule.Enabled)
            {
                if (!_isResting && !_isShutdownMode)
                    StartRestMode(null, false, true);
            }
            else if (_isPasswordRequiredOnly && !IsEveningRestrictedWindow(DateTime.Now) && !IsLunchRestrictedWindow(DateTime.Now))
            {
                // 管理端重新启用当天控制时，立即退出“未启用导致的密码锁”状态。
                _isPasswordRequiredOnly = false;
                _isResting = false;
                StartUsageMode();
            }

            ShowNotification("设置已更新", "管理端下发的配置已生效");
        }

        private void ApplyUsageLimitFromConfig()
        {
            var schedule = _configManager.GetScheduleForToday();
            if (!schedule.Enabled)
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
                SaveState();
            }
        }

        public void ApplyConfigChanges()
        {
            _appMonitor.SetPolicy(_appPolicyManager.GetPolicy());

            var now = DateTime.Now;
            var schedule = _configManager.GetScheduleForToday();

            // 立即应用“启用此天控制”开关，避免保存后仍按旧状态运行。
            if (!schedule.Enabled)
            {
                if (!_isResting && !_isShutdownMode && !_isTimingPaused)
                    StartRestMode(null, false, true);

                ReportStatusToControl();
                return;
            }

            if (_isPasswordRequiredOnly && !IsEveningRestrictedWindow(now) && !IsLunchRestrictedWindow(now))
            {
                _isPasswordRequiredOnly = false;
                _isResting = false;
                StartUsageMode();
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

            if (IsClientLockedForUpdate())
            {
                _ = _autoUpdater.ApplyUpdateAsync(packageUrl, version, force: true);
                return;
            }

            _pendingUpdateUrl = packageUrl;
            _pendingUpdateVersion = version;
            _notifyIcon?.ShowBalloonTip(
                3000,
                "ControlTimeService 升级",
                "已收到升级包，将在下次锁屏时自动安装",
                ToolTipIcon.Info);
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
                config = _configManager.GetAllSchedules(),
                appPolicy = _appPolicyManager.GetPolicy()
            };

            _controlClient.UpdateStatus(status);
        }
    }
}