using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using ControlTimeService;

namespace ControlCenter
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            if (e.Args.Length > 0 && (e.Args[0] == "/headless" || e.Args[0] == "--headless"))
            {
                // 无界面后台模式：仅启动 HTTP 服务器
                ShutdownMode = ShutdownMode.OnExplicitShutdown;

                var serverConfig = ServerConfig.Load();
                var updateConfig = UpdateServerConfig.Load();
                var server = new HttpServer(serverConfig.ListenPort);
                server.WebAdminPassword = serverConfig.WebAdminPassword;
                server.ResolveUpdateVersion = () => updateConfig.DefaultVersion;
                server.ResolveUpdatePackagePath = () => updateConfig.GetLocalPackagePath();

                try
                {
                    server.Start();
                    var lanIp = GetLocalIpAddress();
                    server.PublicBaseUrl = $"http://{lanIp}:{server.Port}";
                    System.Diagnostics.Debug.WriteLine($"Headless 服务器已启动，端口 {server.Port}");

                    // 等待退出信号
                    Console.WriteLine($"ControlCenter 无界面模式已启动");
                    Console.WriteLine($"Web 管理地址: http://localhost:{server.Port}/web");
                    Console.WriteLine($"升级地址: {server.PublicBaseUrl}/api/updates/manifest.json");
                    Console.WriteLine($"按 Ctrl+C 退出");
                    var waitHandle = new System.Threading.ManualResetEvent(false);
                    Console.CancelKeyPress += (s, args) =>
                    {
                        args.Cancel = true;
                        waitHandle.Set();
                    };
                    waitHandle.WaitOne();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"启动失败: {ex.Message}");
                    Environment.Exit(1);
                }
                finally
                {
                    server.Stop();
                    Environment.Exit(0);
                }
                return;
            }

            base.OnStartup(e);
        }

        private static string GetLocalIpAddress()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                        return ip.ToString();
                }
            }
            catch { }
            return "127.0.0.1";
        }
    }
}
