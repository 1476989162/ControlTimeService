using System;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Interop;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ControlTimeService
{
    public partial class LockWindow : Window
    {
        private DateTime _endTime;
        private string _password;
        private DispatcherTimer _uiTimer;
        private KeyboardHook _hook = new KeyboardHook();
        private bool _isAdminMode; // 是否为管理验证模式
        private bool _isShutdownMode; // 是否为夜间关机锁（不再计时，显示关机按钮）
        private bool _isPasswordRequiredOnly; // 仅密码解锁模式（无倒计时）
        private bool _isPauseMode; // 暂停计时模式（无倒计时，恢复后继续计时）
        private bool _requirePasswordForResume; // 暂停模式下解锁是否需密码
        private bool _isAppViolationPause; // 是否因违规应用触发的暂停
        private bool _isMorningLockMode; // 早晨锁定模式
        private readonly Action? _onEnterShutdownMode;

        public int? TemporaryUsageMinutes { get; private set; }
        public bool WasPasswordOnlyUnlock { get; private set; }
        public bool WasMorningLockUnlock { get; private set; }
        public bool WasPauseUnlock { get; private set; }
        public bool WasRemoteAbort { get; private set; }
        private readonly Action _onUiTick;
        private DispatcherTimer _focusTimer;

        // isAdminMode 为 true 时：普通弹窗，不强制全屏，不启动键盘锁
        // isAdminMode 为 false 时：锁屏模式，全屏置顶，强制锁死
        // isShutdownMode 为 true 时：不显示倒计时，显示关机按钮；仍然是锁屏模式但允许输入管理员密码紧急解锁
        public LockWindow(
            DateTime endTime,
            string password,
            bool isAdminMode = false,
            bool isShutdownMode = false,
            Action? onEnterShutdownMode = null,
            bool isPasswordRequiredOnly = false,
            bool isPauseMode = false,
            bool requirePasswordForResume = false,
            bool isAppViolationPause = false,
            Action onUiTick = null,
            bool isMorningLockMode = false)
        {
            InitializeComponent();
            _endTime = endTime;
            _password = password;
            _isAdminMode = isAdminMode;
            _isShutdownMode = isShutdownMode;
            _onEnterShutdownMode = onEnterShutdownMode;
            _isPasswordRequiredOnly = isPasswordRequiredOnly;
            _isPauseMode = isPauseMode;
            _requirePasswordForResume = requirePasswordForResume;
            _isAppViolationPause = isAppViolationPause;
            _isMorningLockMode = isMorningLockMode;
            _onUiTick = onUiTick;

            if (!_isAdminMode)
            {
                // 锁屏模式：全屏、置顶、钩子
                this.WindowState = WindowState.Maximized;
                this.Topmost = true;
                _hook.Hook();

                // 若锁屏前已按 Win 键，开始菜单可能仍在前台，需先关闭再抢焦点
                this.SourceInitialized += (s, e) =>
                {
                    var helper = new WindowInteropHelper(this);
                    var src = HwndSource.FromHwnd(helper.Handle);
                    if (src != null)
                        src.AddHook(WndProc);

                    EnforceLockScreen();
                };

                this.Loaded += (s, e) =>
                {
                    EnforceLockScreen();
                    LockWorkStation();
                    EnforceLockScreen();
                };

                this.Deactivated += (s, e) =>
                {
                    if (!_isAdminMode)
                    {
                        EnforceLockScreen();
                    }
                };

                _focusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                _focusTimer.Tick += (s, e) => EnforceLockScreen();
                _focusTimer.Start();
            }
            else
            {
                // 管理验证模式：普通窗口大小
                this.WindowStyle = WindowStyle.SingleBorderWindow;
                this.Width = 400;
                this.Height = 300;
                this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                TxtTimer.Visibility = Visibility.Collapsed; // 验证模式不需要倒计时
            }

            if (_isShutdownMode)
            {
                EnterShutdownMode();
            }
            else if (_isPasswordRequiredOnly)
            {
                EnterPasswordOnlyMode();
            }
            else if (_isPauseMode)
            {
                EnterPauseMode();
            }
            else if (_isMorningLockMode)
            {
                EnterMorningLockMode();
            }

            // 禁止右键/系统菜单通过预处理事件
            this.PreviewMouseRightButtonDown += (s, e) => { e.Handled = true; };
            this.PreviewMouseDown += (s, e) => { /* 阻止鼠标对窗口的其他交互 */ };

            // 倒计时逻辑
            _uiTimer = new DispatcherTimer();
            _uiTimer.Interval = TimeSpan.FromSeconds(1);
            _uiTimer.Tick += (s, e) =>
            {
                _onUiTick?.Invoke();

                if (!_isAdminMode && !_isShutdownMode && IsNightlyShutdownTime(DateTime.Now))
                {
                    EnterShutdownMode();
                    _onEnterShutdownMode?.Invoke();
                    return;
                }

                if (!_isAdminMode && !_isShutdownMode && !_isPasswordRequiredOnly && !_isPauseMode)
                {
                    var remaining = _endTime - DateTime.Now;
                    if (remaining.TotalSeconds <= 0) UnlockAndClose();
                    else TxtTimer.Text = $"距离解锁还剩: {remaining.Minutes:D2}:{remaining.Seconds:D2}";
                }
            };
            _uiTimer.Start();
        }

        private void EnforceLockScreen()
        {
            if (_isAdminMode)
                return;

            LockScreenHelper.DismissStartMenuAndOverlays();
            LockScreenHelper.ForceLockForeground(this);
        }

        private bool IsNightlyShutdownTime(DateTime now)
        {
            // 周一到周日，每天 20:30 后进入夜间关机锁
            return now.TimeOfDay >= new TimeSpan(20, 30, 0);
        }

        private void EnterMorningLockMode()
        {
            TxtTitle.Text = "早晨已锁定";
            TxtTimer.Visibility = Visibility.Visible;
            BtnShutdown.Visibility = Visibility.Collapsed;
        }

        private void EnterShutdownMode()
        {
            _isShutdownMode = true;
            TxtTitle.Text = "夜间已锁定";
            TxtTimer.Visibility = Visibility.Collapsed;
            BtnShutdown.Visibility = Visibility.Visible;
        }

        private void EnterPasswordOnlyMode()
        {
            TxtTitle.Text = "晚间时段超限，请输入密码";
            TxtTimer.Visibility = Visibility.Collapsed;
            BtnShutdown.Visibility = Visibility.Collapsed;
        }

        private void EnterPauseMode()
        {
            if (_isAppViolationPause)
            {
                TxtTitle.Text = "检测到禁止的应用";
                TxtTimer.Text = "点击下方按钮继续计时";
            }
            else if (_requirePasswordForResume)
            {
                TxtTitle.Text = "计时已暂停";
                TxtTimer.Text = "输入密码后可继续计时";
            }
            else
            {
                TxtTitle.Text = "计时已暂停";
                TxtTimer.Text = "点击下方按钮继续计时";
            }

            TxtTimer.Visibility = Visibility.Visible;
            PassBox.Visibility = _requirePasswordForResume ? Visibility.Visible : Visibility.Collapsed;
            BtnUnlock.Content = "继续计时";
            BtnShutdown.Visibility = Visibility.Collapsed;
        }

        private void Unlock_Click(object sender, RoutedEventArgs e)
        {
            if (_isPauseMode)
            {
                if (_requirePasswordForResume && PassBox.Password != _password)
                {
                    System.Windows.MessageBox.Show("密码错误！");
                    return;
                }

                WasPauseUnlock = true;
                UnlockAndClose();
                return;
            }

            if (PassBox.Password == _password)
            {
                if (_isPasswordRequiredOnly)
                {
                    WasPasswordOnlyUnlock = true;
                }
                else if (_isMorningLockMode)
                {
                    WasMorningLockUnlock = true;
                }

                if (!_isAdminMode)
                {
                    var minutes = PromptTemporaryUsageMinutes();
                    if (!minutes.HasValue)
                    {
                        return;
                    }
                    TemporaryUsageMinutes = minutes.Value;
                }

                UnlockAndClose();
            }
            else
            {
                System.Windows.MessageBox.Show("密码错误！");
            }
        }

        private int? PromptTemporaryUsageMinutes()
        {
            if (_isPasswordRequiredOnly)
            {
                return 30;
            }

            var popup = new Window
            {
                Title = "临时解锁时长",
                Width = 300,
                Height = 170,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                Owner = this,
                Topmost = true
            };

            var combo = new System.Windows.Controls.ComboBox
            {
                Margin = new Thickness(0, 10, 0, 15),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                ItemsSource = new[] { 10, 15, 20, 30 },
                SelectedItem = 30
            };

            var okBtn = new System.Windows.Controls.Button { Content = "确定", Width = 80, Margin = new Thickness(5), IsDefault = true };
            var cancelBtn = new System.Windows.Controls.Button { Content = "取消", Width = 80, Margin = new Thickness(5), IsCancel = true };

            int selectedMinutes = 30;

            okBtn.Click += (s, e) =>
            {
                if (combo.SelectedItem is int m)
                {
                    selectedMinutes = m;
                    popup.DialogResult = true;
                }
                else
                {
                    System.Windows.MessageBox.Show("请选择时长。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };

            cancelBtn.Click += (s, e) => popup.DialogResult = false;

            var buttonPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Children = { okBtn, cancelBtn }
            };

            var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(15) };
            root.Children.Add(new System.Windows.Controls.TextBlock { Text = "请输入继续可使用时间（分钟）", Margin = new Thickness(0, 0, 0, 5) });
            root.Children.Add(combo);
            root.Children.Add(buttonPanel);

            popup.Content = root;

            return popup.ShowDialog() == true ? selectedMinutes : null;
        }

        private void UnlockAndClose()
        {
            _uiTimer?.Stop();
            _focusTimer?.Stop();
            if (!_isAdminMode) _hook.Unhook();
            this.DialogResult = true; // 验证成功返回 true
            this.Close();
        }

        public void ForceRemoteUnlock(int usageMinutes)
        {
            WasRemoteAbort = false;
            WasPasswordOnlyUnlock = false;

            if (_isPauseMode && usageMinutes > 0)
            {
                TemporaryUsageMinutes = usageMinutes;
                WasPauseUnlock = false;
            }
            else if (_isPauseMode)
            {
                WasPauseUnlock = true;
            }
            else
            {
                TemporaryUsageMinutes = usageMinutes;
            }

            UnlockAndClose();
        }

        public void ForceAbortForRemoteCommand()
        {
            WasRemoteAbort = true;
            WasPauseUnlock = false;
            WasPasswordOnlyUnlock = false;
            TemporaryUsageMinutes = null;
            UnlockAndClose();
        }

        public void UpdateEndTime(DateTime endTime)
        {
            _endTime = endTime;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool LockWorkStation();

        private void BtnShutdown_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 立即关机
                Process.Start(new ProcessStartInfo("shutdown", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("无法执行关机：" + ex.Message);
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // 如果不是通过设置 DialogResult = true（即管理员解锁或正常倒计时完成），并且不是管理员弹窗，则禁止关闭
            if (this.DialogResult != true && !_isAdminMode)
            {
                e.Cancel = true;
            }
            base.OnClosing(e);
        }

        // 拦截系统消息，阻止 SC_CLOSE
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_SYSCOMMAND = 0x0112;
            const int SC_CLOSE = 0xF060;
            if (msg == WM_SYSCOMMAND && ((wParam.ToInt32() & 0xFFF0) == SC_CLOSE))
            {
                handled = true;
                return IntPtr.Zero;
            }
            return IntPtr.Zero;
        }
    }
}