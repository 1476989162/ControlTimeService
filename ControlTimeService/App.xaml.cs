using System.Configuration;
using System.Data;
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
        private static Mutex _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
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
                _singleInstanceMutex = new Mutex(true, "Global\\ControlTimeService_SingleInstance", out createdNew);
            }
            catch
            {
                // Global 命名空间受限时退回会话级互斥，保证单实例仍生效
                _singleInstanceMutex = new Mutex(true, "ControlTimeService_SingleInstance", out createdNew);
            }

            if (!createdNew)
            {
                // 看门狗每分钟唤醒会触发二次启动，静默退出避免每分钟弹窗
                var isWatchdog = e.Args != null && e.Args.Any(a => string.Equals(a, "--watchdog", System.StringComparison.OrdinalIgnoreCase));
                if (!isWatchdog)
                {
                    System.Windows.MessageBox.Show("ControlTimeService 已在运行，请勿重复启动。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    CrashLogger.Log("看门狗检测到实例已在运行，静默退出");
                }
                Shutdown();
                return;
            }

            base.OnStartup(e);
        }
    }

}
