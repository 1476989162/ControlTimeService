using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ControlTimeService
{
    public class AutoUpdater
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly ControlConfig _config;
        private readonly Action<string>? _notify;
        private readonly SemaphoreSlim _updateLock = new SemaphoreSlim(1, 1);
        private System.Threading.Timer? _checkTimer;
        private bool _started;

        public AutoUpdater(ControlConfig config, Action<string>? notify = null)
        {
            _config = config ?? new ControlConfig();
            _notify = notify;
        }

        public static Version GetCurrentVersion()
        {
            return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        }

        public static string GetCurrentVersionString()
        {
            return GetCurrentVersion().ToString(3);
        }

        public void StartPeriodicCheck()
        {
            if (_started) return;
            _started = true;

            if (_config.UpdateCheckIntervalMinutes <= 0)
                return;

            if (string.IsNullOrWhiteSpace(_config.UpdateManifestUrl) &&
                string.IsNullOrWhiteSpace(_config.UpdatePackageUrl))
                return;

            _ = CheckForUpdatesAsync();

            var interval = TimeSpan.FromMinutes(_config.UpdateCheckIntervalMinutes);
            _checkTimer = new System.Threading.Timer(_ => _ = CheckForUpdatesAsync(), null, interval, interval);
        }

        public void Stop()
        {
            _checkTimer?.Dispose();
            _checkTimer = null;
        }

        public async Task CheckForUpdatesAsync()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_config.UpdateManifestUrl))
                {
                    var manifestUrl = _config.UpdateManifestUrl.Trim();
                    var manifest = await FetchManifestAsync(manifestUrl);
                    if (manifest != null &&
                        !string.IsNullOrWhiteSpace(manifest.PackageUrl) &&
                        IsNewerVersion(manifest.Version, GetCurrentVersionString()))
                    {
                        Notify($"发现新版本 {manifest.Version}，开始下载升级包...");
                        await ApplyUpdateAsync(
                            ResolveDownloadUrl(manifest.PackageUrl, manifestUrl),
                            manifest.Version);
                    }
                    return;
                }

                if (!string.IsNullOrWhiteSpace(_config.UpdatePackageUrl) &&
                    !string.IsNullOrWhiteSpace(_config.UpdateVersion) &&
                    IsNewerVersion(_config.UpdateVersion, GetCurrentVersionString()))
                {
                    Notify($"发现新版本 {_config.UpdateVersion}，开始下载升级包...");
                    await ApplyUpdateAsync(_config.UpdatePackageUrl, _config.UpdateVersion);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"检查更新失败: {ex.Message}");
            }
        }

        public async Task ApplyUpdateAsync(string packageUrl, string version, bool force = false)
        {
            if (string.IsNullOrWhiteSpace(packageUrl))
                return;

            if (!force && !string.IsNullOrWhiteSpace(version) &&
                !IsNewerVersion(version, GetCurrentVersionString()))
            {
                Debug.WriteLine($"当前版本 {GetCurrentVersionString()} 已是最新，跳过升级");
                return;
            }

            if (!await _updateLock.WaitAsync(0))
            {
                Debug.WriteLine("升级正在进行中，跳过重复请求");
                return;
            }

            try
            {
                Notify("正在下载升级包...");
                var workDir = Path.Combine(Path.GetTempPath(), "ControlTimeService_update", version ?? "latest");
                Directory.CreateDirectory(workDir);

                var zipPath = Path.Combine(workDir, "package.zip");
                var extractDir = Path.Combine(workDir, "extracted");

                if (Directory.Exists(extractDir))
                    Directory.Delete(extractDir, true);
                Directory.CreateDirectory(extractDir);

                using (var http = CreateNoProxyHttpClient(TimeSpan.FromMinutes(30)))
                {
                    var downloadUrl = NormalizeDownloadUrl(ResolveDownloadUrl(packageUrl));
                    using var response = await http.GetAsync(downloadUrl);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            $"下载升级包失败: HTTP {(int)response.StatusCode} ({response.ReasonPhrase})，URL: {downloadUrl}");
                    }

                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    if (bytes.Length < 4 || bytes[0] != 0x50 || bytes[1] != 0x4B)
                    {
                        throw new InvalidOperationException(
                            $"涓嬭浇鐨勫唴瀹逛笉鏄湁鏁堢殑 ZIP 鏂囦欢锛岃妫€鏌ュ崌绾у寘 URL 鏄惁姝ｇ‘: {downloadUrl}");
                    }

                    await File.WriteAllBytesAsync(zipPath, bytes);
                }

                Notify("正在解压升级包...");
                ZipFile.ExtractToDirectory(zipPath, extractDir);
                var sourceDir = ResolvePackageRoot(extractDir);

                var appDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule!.FileName)!;
                var exePath = Path.Combine(appDir, "ControlTimeService.exe");
                var updaterScript = Path.Combine(workDir, "apply_update.bat");

                var script = $@"@echo off
chcp 65001 >nul
ping 127.0.0.1 -n 4 >nul
xcopy /E /Y /I ""{sourceDir}\*"" ""{appDir}\""
start """" ""{exePath}""
(goto) 2>nul & del ""%~f0""
";
                await File.WriteAllTextAsync(updaterScript, script);

                Notify($"升级包已就绪，正在安装版本 {version ?? "未知"}...");
                Process.Start(new ProcessStartInfo
                {
                    FileName = updaterScript,
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    System.Windows.Application.Current.Shutdown();
                });
            }
            catch (Exception ex)
            {
                Notify($"升级失败: {ex.Message}");
                Debug.WriteLine($"升级失败: {ex}");
            }
            finally
            {
                _updateLock.Release();
            }
        }

        public static string NormalizeDownloadUrl(string packageUrl)
        {
            if (string.IsNullOrWhiteSpace(packageUrl))
                return packageUrl;

            if (packageUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return packageUrl;

            if (packageUrl.Contains("/api/updates/", StringComparison.OrdinalIgnoreCase))
                return packageUrl;

            return $"{packageUrl.TrimEnd('/')}/api/updates/ControlTimeService.zip";
        }

        private static string ResolveDownloadUrl(string packageUrl, string manifestUrl = null)
        {
            if (string.IsNullOrWhiteSpace(packageUrl))
                return packageUrl;

            if (Uri.TryCreate(packageUrl, UriKind.Absolute, out var absolute) &&
                !string.IsNullOrEmpty(absolute.Host))
                return packageUrl;

            if (!string.IsNullOrWhiteSpace(manifestUrl) &&
                Uri.TryCreate(manifestUrl, UriKind.Absolute, out var manifestUri))
                return new Uri(manifestUri, packageUrl).ToString();

            return packageUrl;
        }

       private static async Task<UpdateManifest> FetchManifestAsync(string manifestUrl)
       {
           using var http = CreateNoProxyHttpClient(TimeSpan.FromSeconds(30));
           var json = await http.GetStringAsync(manifestUrl);
           return JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions);
       }

        /// <summary>
        /// 创建不使用系统代理的 HttpClient，避免内网升级地址经代理返回 502。
        /// </summary>
        private static HttpClient CreateNoProxyHttpClient(TimeSpan timeout)
        {
            var handler = new HttpClientHandler
            {
                UseProxy = false,
                AutomaticDecompression = System.Net.DecompressionMethods.None
            };
            return new HttpClient(handler) { Timeout = timeout };
        }

        private static string ResolvePackageRoot(string extractDir)
        {
            var files = Directory.GetFiles(extractDir);
            var dirs = Directory.GetDirectories(extractDir);

            if (files.Length == 0 && dirs.Length == 1)
                return dirs[0];

            return extractDir;
        }

        private static bool IsNewerVersion(string remoteVersion, string currentVersion)
        {
            if (string.IsNullOrWhiteSpace(remoteVersion))
                return true;

            if (!Version.TryParse(remoteVersion, out var remote))
                return !string.Equals(remoteVersion, currentVersion, StringComparison.OrdinalIgnoreCase);

            if (!Version.TryParse(currentVersion, out var current))
                return true;

            return remote > current;
        }

        private void Notify(string message)
        {
            Debug.WriteLine(message);
            _notify?.Invoke(message);
        }
    }
}
