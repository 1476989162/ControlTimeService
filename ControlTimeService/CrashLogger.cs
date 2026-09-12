using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ControlTimeService
{
    /// <summary>
    /// 崩溃日志 + 自愈：捕获所有未处理异常，写入 crash.log，并尝试立即拉起自身
    /// </summary>
    public static class CrashLogger
    {
        private static string _logPath;
        private static bool _initialized;
        private static DateTime _lastErrorDialogTime = DateTime.MinValue;

        /// <summary>
        /// 异常提示框做 60 秒节流：UI 线程若持续抛异常，
        /// 每次都弹模态框会叠加成“程序无响应”，反而掩盖真实错误。
        /// </summary>
        private static void ShowErrorDialog(Exception ex)
        {
            try
            {
                if ((DateTime.Now - _lastErrorDialogTime).TotalSeconds < 60)
                    return;

                _lastErrorDialogTime = DateTime.Now;
                System.Windows.MessageBox.Show(
                    $"程序出现异常已记录：\n{ex?.Message}\n\n日志：{_logPath}",
                    "异常已捕获",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch { }
        }

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                var appDir = AppDomain.CurrentDomain.BaseDirectory;
                var logDir = Path.Combine(appDir, "logs");
                try { Directory.CreateDirectory(logDir); } catch { logDir = appDir; }
                _logPath = Path.Combine(logDir, "crash.log");

                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    var ex = e.ExceptionObject as Exception;
                    Log($"[AppDomain Unhandled] IsTerminating={e.IsTerminating} {ex}");
                    TryRestart("AppDomain Unhandled");
                };

                TaskScheduler.UnobservedTaskException += (s, e) =>
                {
                    Log($"[Task Unobserved] {e.Exception}");
                    e.SetObserved();
                };

                // 清理过大的日志（>5MB则轮转）
                TryRotateLog();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CrashLogger 初始化失败: {ex.Message}");
            }
        }

        public static void AttachDispatcher(Dispatcher dispatcher)
        {
            try
            {
                dispatcher.UnhandledException += (s, e) =>
                {
                    Log($"[Dispatcher Unhandled] {e.Exception}");
                    e.Handled = true;
                    // 派发线程异常不直接重启，但记录后尝试自愈检查
                    // 看门狗自愈放到后台：内部 schtasks 会阻塞数秒，
                    // 异常处理路径上再卡住 UI 线程会直接表现为程序无响应
                    try { Task.Run(() => { try { WatchdogHelper.EnsureInstalled(); } catch { } }); } catch { }
                    ShowErrorDialog(e.Exception);
                };
            }
            catch { }
        }

        public static void AttachApplication()
        {
            try
            {
                if (System.Windows.Application.Current != null)
                {
                    System.Windows.Application.Current.DispatcherUnhandledException += (s, e) =>
                    {
                        Log($"[Application DispatcherUnhandled] {e.Exception}");
                        e.Handled = true;
                        try { WatchdogHelper.EnsureInstalled(); } catch { }
                        System.Windows.MessageBox.Show($"程序出现异常已记录：\n{e.Exception.Message}\n\n日志：{_logPath}", "异常已捕获", MessageBoxButton.OK, MessageBoxImage.Warning);
                    };
                }
            }
            catch { }
        }

        public static void Log(string message)
        {
            try
            {
                if (string.IsNullOrEmpty(_logPath))
                {
                    var dir = AppDomain.CurrentDomain.BaseDirectory;
                    _logPath = Path.Combine(dir, "crash.log");
                }
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                File.AppendAllText(_logPath, line, Encoding.UTF8);
                Debug.WriteLine(line);
            }
            catch { }
        }

        private static void TryRotateLog()
        {
            try
            {
                if (string.IsNullOrEmpty(_logPath) || !File.Exists(_logPath)) return;
                var info = new FileInfo(_logPath);
                if (info.Length > 5 * 1024 * 1024)
                {
                    var backup = _logPath + ".1";
                    try { File.Delete(backup); } catch { }
                    File.Move(_logPath, backup);
                }
            }
            catch { }
        }

        private static void TryRestart(string reason)
        {
            try
            {
                Log($"尝试自重启，原因: {reason}");
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                    exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ControlTimeService.exe");
                if (!File.Exists(exePath)) return;

                // 延迟 1 秒后拉起，避免与当前崩溃进程的互斥锁冲突
                Task.Delay(1200).ContinueWith(_ =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = exePath,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        Log($"自重启失败: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Log($"TryRestart 异常: {ex.Message}");
            }
        }
    }
}
