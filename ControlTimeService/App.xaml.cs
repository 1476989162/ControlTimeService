using System;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Application = System.Windows.Application;
using Forms = System.Windows.Forms;
namespace ControlTimeService
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private const string GlobalMutexName = "Global\\ControlTimeService_SingleInstance";
        private const string LocalMutexName = "ControlTimeService_SingleInstance";
        private const string WatchdogArg = "--watchdog";

        private static Mutex _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 看门狗分支必须最先处理：计划任务每分钟用它做一次存活检查。
            // 本分支不初始化日志、不创建任何窗口，检查完立即退出。
            if (IsWatchdogInvocation(e))
            {
                RunWatchdogCheck();
                return;
            }

            // 崩溃日志全局兜底，必须最先初始化
            CrashLogger.Initialize();
            CrashLogger.AttachApplication();
            if (Dispatcher != null)
                CrashLogger.AttachDispatcher(Dispatcher);

            // 单实例保护：防止升级/重复启动后新旧实例并存，
            // 双实例会互相覆盖 state.txt、争抢远程命令，导致锁屏失效或状态错乱。
            bool createdNew = true;
            try
            {
                _singleInstanceMutex = new Mutex(true, GlobalMutexName, out createdNew);
            }
            catch
            {
                // Global 命名空间受限时退回会话级互斥，保证单实例仍生效
                _singleInstanceMutex = new Mutex(true, LocalMutexName, out createdNew);
            }

            if (!createdNew)
            {
                System.Windows.MessageBox.Show("ControlTimeService 已在运行，请勿重复启动。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            base.OnStartup(e);
        }

        private static bool IsWatchdogInvocation(StartupEventArgs e)
        {
            return e.Args != null && e.Args.Any(a =>
                string.Equals(a, WatchdogArg, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 看门狗存活检查：主进程不在就拉起一个正式实例，然后本进程立刻退出。
        /// 这里用进程名枚举代替旧版的 tasklist，从而省掉 cmd.exe —— 那正是每分钟闪黑框的来源。
        /// 本进程是 GUI 子系统程序，不会分配控制台窗口。
        /// </summary>
        private static void RunWatchdogCheck()
        {
            try
            {
                var self = Process.GetCurrentProcess();
                var exeName = Path.GetFileNameWithoutExtension(self.MainModule?.FileName ?? string.Empty);
                if (string.IsNullOrWhiteSpace(exeName))
                    exeName = "ControlTimeService";

                int selfId = self.Id;
                bool mainRunning = false;

                var others = Process.GetProcessesByName(exeName);
                try
                {
                    foreach (var p in others)
                    {
                        try
                        {
                            if (p.Id != selfId)
                            {
                                mainRunning = true;
                                break;
                            }
                        }
                        catch
                        {
                            // 个别进程读不到 Id 时按“存在”处理，避免重复拉起
                            mainRunning = true;
                            break;
                        }
                    }
                }
                finally
                {
                    foreach (var p in others)
                    {
                        try { p.Dispose(); } catch { }
                    }
                }

                if (!mainRunning)
                    StartMainInstance();
            }
            catch
            {
                // 看门狗异常不外抛：让计划任务正常结束，下一分钟再试
            }
        }

        /// <summary>
        /// 脱离看门狗进程启动正式实例（不带 --watchdog）。
        /// </summary>
        private static void StartMainInstance()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                    exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ControlTimeService.exe");
                if (!File.Exists(exePath))
                    return;

                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppDomain.CurrentDomain.BaseDirectory
                });
            }
            catch
            {
                // 拉起失败留给下一分钟的检查重试
            }
        }
    }

}

