using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace ControlTimeService
{
    /// <summary>
    /// 看门狗：开机自启 + 计划任务每分钟自检拉起。
    /// 这是“最好”的组合：任务计划在系统层兜底，即使进程被任务管理器强杀、崩溃、或被360/火绒结束，
    /// 1分钟内自动拉起；配合注册表 Run 保证重启后也能启动；配合崩溃日志便于定位被杀原因。
    /// </summary>
    public static class WatchdogHelper
    {
        public const string TaskName = "ControlTimeService_Watchdog";
        private const string WatchdogBatName = "watchdog.bat";
        private static readonly TimeSpan ReEnsureInterval = TimeSpan.FromMinutes(10);
        private static DateTime _lastEnsure = DateTime.MinValue;
        private static readonly object _lock = new();

        public static void EnsureInstalled()
        {
            lock (_lock)
            {
                try
                {
                    EnsureAutoStart();
                    EnsureWatchdogBatch();
                    EnsureScheduledTask();
                    _lastEnsure = DateTime.Now;
                }
                catch (Exception ex)
                {
                    CrashLogger.Log($"看门狗安装失败: {ex}");
                }
            }
        }

        /// <summary>
        /// 周期性自愈：每10分钟检查一次，防止用户手动删除任务/注册表后防护失效
        /// </summary>
        public static void EnsureInstalledIfNeeded()
        {
            if ((DateTime.Now - _lastEnsure) < ReEnsureInterval) return;
            EnsureInstalled();
        }

        private static string GetExePath()
        {
            try { return Process.GetCurrentProcess().MainModule?.FileName ?? ""; }
            catch { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ControlTimeService.exe"); }
        }

        private static string GetAppDir()
        {
            var exe = GetExePath();
            try { return Path.GetDirectoryName(exe) ?? AppDomain.CurrentDomain.BaseDirectory; }
            catch { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        private static string GetWatchdogBatPath() => Path.Combine(GetAppDir(), WatchdogBatName);

        // 注册表开机自启（HKCU Run），登录即启动
        private static void EnsureAutoStart()
        {
            try
            {
                var exePath = GetExePath();
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return;
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                if (key == null) return;
                var current = key.GetValue("ControlTimeService") as string;
                var expected = $"\"{exePath}\"";
                // 已正确则不重复写入，减少注册表抖动
                if (string.Equals(current, expected, StringComparison.OrdinalIgnoreCase)) return;
                key.SetValue("ControlTimeService", expected);
                CrashLogger.Log($"已确保开机自启: {expected}");
            }
            catch (Exception ex)
            {
                CrashLogger.Log($"EnsureAutoStart 失败: {ex.Message}");
            }
        }

        private static void EnsureWatchdogBatch()
        {
            try
            {
                var exePath = GetExePath();
                var exeName = Path.GetFileName(exePath);
                if (string.IsNullOrWhiteSpace(exeName)) exeName = "ControlTimeService.exe";
                var batPath = GetWatchdogBatPath();

                // bat 逻辑：用 tasklist 检测进程是否存在，不存在则拉起。避免触发单实例的 MessageBox。
                // 带 --watchdog 参数可让二次启动静默退出，不弹窗打扰
                var batContent = $@"@echo off
setlocal
chcp 65001 >nul 2>&1
tasklist /FI ""IMAGENAME eq {exeName}"" 2>nul | find /I ""{exeName}"" >nul
if errorlevel 1 (
  start """" ""{exePath}"" --watchdog
)
";

                var needWrite = true;
                if (File.Exists(batPath))
                {
                    try
                    {
                        var existing = File.ReadAllText(batPath, Encoding.Default);
                        if (string.Equals(existing.Trim(), batContent.Trim(), StringComparison.Ordinal))
                            needWrite = false;
                    }
                    catch { }
                }
                if (needWrite)
                {
                    File.WriteAllText(batPath, batContent, new UTF8Encoding(false));
                    CrashLogger.Log($"已生成看门狗脚本: {batPath}");
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log($"EnsureWatchdogBatch 失败: {ex.Message}");
            }
        }

        private static void EnsureScheduledTask()
        {
            try
            {
                if (IsTaskExists() && IsTaskActionCorrect())
                {
                    return;
                }

                // 优先用 XML 创建带 InteractiveToken + HighestAvailable 的任务，最稳且能在用户桌面显示UI
                if (TryCreateTaskViaXml())
                {
                    CrashLogger.Log("计划任务已通过XML创建/更新");
                    return;
                }

                // 回退：用 schtasks 简单命令创建
                TryCreateTaskViaSimpleCommand();
            }
            catch (Exception ex)
            {
                CrashLogger.Log($"EnsureScheduledTask 失败: {ex.Message}");
                try { TryCreateTaskViaSimpleCommand(); } catch { }
            }
        }

        private static bool IsTaskExists()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks", $"/query /tn \"{TaskName}\" /v /fo LIST")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(5000);
                return p != null && p.ExitCode == 0;
            }
            catch { return false; }
        }

        private static bool IsTaskActionCorrect()
        {
            try
            {
                var batPath = GetWatchdogBatPath();
                var psi = new ProcessStartInfo("schtasks", $"/query /tn \"{TaskName}\" /v /fo LIST")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using var p = Process.Start(psi);
                var output = p?.StandardOutput.ReadToEnd() ?? "";
                p?.WaitForExit(5000);
                if (p == null || p.ExitCode != 0) return false;
                // 检查任务的“运行”字段是否包含 watchdog.bat
                return output.IndexOf(WatchdogBatName, StringComparison.OrdinalIgnoreCase) >= 0
                    || output.IndexOf("watchdog", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        private static bool TryCreateTaskViaXml()
        {
            try
            {
                var batPath = GetWatchdogBatPath();
                var appDir = GetAppDir();
                var xmlPath = Path.Combine(Path.GetTempPath(), $"ControlTimeService_Task_{Guid.NewGuid():N}.xml");

                var escapedBat = System.Security.SecurityElement.Escape(batPath);
                var escapedDir = System.Security.SecurityElement.Escape(appDir);
                var now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                // 明天的日期用于 CalendarTrigger 的 StartBoundary，避免过去时间导致首次不触发
                var startBoundary = DateTime.Now.Date.ToString("yyyy-MM-ddTHH:mm:ss");

                var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Date>{now}</Date>
    <Author>ControlTimeService</Author>
    <Description>ControlTimeService 看门狗：每分钟检查进程是否存活，自动拉起。由客户端自动创建，删除后10分钟内自愈重建。</Description>
    <URI>\{TaskName}</URI>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <Delay>PT30S</Delay>
    </LogonTrigger>
    <CalendarTrigger>
      <StartBoundary>{startBoundary}</StartBoundary>
      <Enabled>true</Enabled>
      <ScheduleByDay>
        <DaysInterval>1</DaysInterval>
      </ScheduleByDay>
      <Repetition>
        <Interval>PT1M</Interval>
        <Duration>P1D</Duration>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
    </CalendarTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>false</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{escapedBat}</Command>
      <WorkingDirectory>{escapedDir}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";

                // 必须 UTF-16 LE 带 BOM，否则 schtasks 解析失败
                File.WriteAllText(xmlPath, xml, Encoding.Unicode);

                var psi = new ProcessStartInfo("schtasks", $"/create /tn \"{TaskName}\" /xml \"{xmlPath}\" /f")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using var p = Process.Start(psi);
                var outStr = p?.StandardOutput.ReadToEnd() ?? "";
                var errStr = p?.StandardError.ReadToEnd() ?? "";
                p?.WaitForExit(8000);
                try { File.Delete(xmlPath); } catch { }

                if (p != null && p.ExitCode == 0) return true;

                CrashLogger.Log($"XML创建任务失败 ExitCode={p?.ExitCode} out={outStr} err={errStr}");
                return false;
            }
            catch (Exception ex)
            {
                CrashLogger.Log($"TryCreateTaskViaXml 异常: {ex.Message}");
                return false;
            }
        }

        private static void TryCreateTaskViaSimpleCommand()
        {
            try
            {
                var batPath = GetWatchdogBatPath();
                // 使用 schtasks 简单语法回退；注意 bat 路径引号转义
                var tr = $"\\\"{batPath}\\\"";
                // /sc minute /mo 1 每分钟
                var args = $"/create /tn \"{TaskName}\" /tr \"{tr}\" /sc minute /mo 1 /f /rl HIGHEST";
                var psi = new ProcessStartInfo("schtasks", args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(8000);
                var ok = p != null && p.ExitCode == 0;
                CrashLogger.Log(ok ? "计划任务已通过简单命令创建" : $"简单命令创建失败 ExitCode={p?.ExitCode}");

                // 若简单命令也未包含登录触发，额外创建一个登录触发任务作为补充
                if (ok)
                {
                    var logonTask = TaskName + "_Logon";
                    var exePath = GetExePath();
                    var tr2 = $"\\\"{exePath}\\\"";
                    var args2 = $"/create /tn \"{logonTask}\" /tr \"{tr2}\" /sc onlogon /f /rl HIGHEST /delay 0000:30";
                    var psi2 = new ProcessStartInfo("schtasks", args2)
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var p2 = Process.Start(psi2);
                    p2?.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log($"TryCreateTaskViaSimpleCommand 异常: {ex.Message}");
            }
        }

        public static void TryRemoveWatchdog()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks", $"/delete /tn \"{TaskName}\" /f")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(3000);
                var psi2 = new ProcessStartInfo("schtasks", $"/delete /tn \"{TaskName}_Logon\" /f")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var p2 = Process.Start(psi2);
                p2?.WaitForExit(3000);
            }
            catch { }
        }
    }
}
