using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ControlTimeService
{
    public class ControlClient
    {
        private static readonly JsonSerializerOptions CommandJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private HttpClient _httpClient;
        private string _controlServerUrl;
        private string _clientId;
        private DispatcherTimer _heartbeatTimer;
        private TimeConfigManager _configManager;
        private AppPolicyManager _appPolicyManager;
        private MainWindow _mainWindow;
        private bool _isRunning = false;
        private bool _isRegistered = false;
        private volatile bool _pollRequested = false;
        private const int PollIntervalMs = 1000;
        private static readonly JsonSerializerOptions StatusJsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private string ClientApiPath(string suffix)
        {
            return $"{_controlServerUrl}/api/clients/{Uri.EscapeDataString(_clientId)}{suffix}";
        }

        public ControlClient(string serverUrl, TimeConfigManager configManager, AppPolicyManager appPolicyManager, MainWindow mainWindow)
        {
            _controlServerUrl = serverUrl.TrimEnd('/');
            _configManager = configManager;
            _appPolicyManager = appPolicyManager;
            _mainWindow = mainWindow;
            _clientId = GenerateClientId();
            _httpClient = new HttpClient(new HttpClientHandler { UseProxy = false, AutomaticDecompression = System.Net.DecompressionMethods.None }) { Timeout = TimeSpan.FromSeconds(10) };
        }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;

            _ = RegisterWithServerAsync();
            
            _heartbeatTimer = new DispatcherTimer();
            _heartbeatTimer.Interval = TimeSpan.FromSeconds(30);
            _heartbeatTimer.Tick += async (s, e) =>
            {
                if (!_isRegistered)
                    await RegisterWithServerAsync();
                else
                    await SendHeartbeatAsync();
            };
            _heartbeatTimer.Start();

            // 启动指令监听
            Task.Run(() => PollCommands());

            System.Diagnostics.Debug.WriteLine($"控制客户端已启动，服务器: {_controlServerUrl}");
        }

        public void RequestImmediatePoll()
        {
            _pollRequested = true;
        }

        public void Stop()
        {
            _isRunning = false;
            if (_heartbeatTimer != null)
            {
                _heartbeatTimer.Stop();
            }
        }

        private string GenerateClientId()
        {
            var macAddr = GetMacAddress();
            return $"{Environment.MachineName}_{macAddr}";
        }

        private string GetMacAddress()
        {
            try
            {
                var nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                foreach (var nic in nics)
                {
                    if (nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                        nic.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    {
                        var mac = nic.GetPhysicalAddress().ToString();
                        if (!string.IsNullOrEmpty(mac))
                            return mac;
                    }
                }
            }
            catch { }
            return "unknown";
        }

        private async Task RegisterWithServerAsync()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"正在注册到控制端服务器: {_controlServerUrl}");
                
                var info = new ClientInfo
                {
                    Id = _clientId,
                    Name = Environment.MachineName,
                    IpAddress = GetLocalIpAddress(),
                    AppVersion = AutoUpdater.GetCurrentVersionString(),
                    Config = _configManager.GetAllSchedules(),
                    AppPolicy = _appPolicyManager.GetPolicy()
                };

                var content = new StringContent(
                    JsonSerializer.Serialize(info),
                    Encoding.UTF8,
                    "application/json");

                var response = await _httpClient.PostAsync(
                    $"{_controlServerUrl}/api/clients/register", content);

                if (response.IsSuccessStatusCode)
                {
                    _isRegistered = true;
                    System.Diagnostics.Debug.WriteLine("成功注册到控制端服务器！");
                }
                else
                {
                    _isRegistered = false;
                    System.Diagnostics.Debug.WriteLine($"注册失败，HTTP状态码: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                _isRegistered = false;
                System.Diagnostics.Debug.WriteLine($"注册到控制端失败: {ex.Message}");
            }
        }

        private async Task SendHeartbeatAsync()
        {
            try
            {
                var heartbeatData = new { id = _clientId, name = Environment.MachineName };

                var content = new StringContent(
                    JsonSerializer.Serialize(heartbeatData),
                    Encoding.UTF8,
                    "application/json");

                var response = await _httpClient.PostAsync(
                    $"{_controlServerUrl}/api/clients/heartbeat", content);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _isRegistered = false;
                    await RegisterWithServerAsync();
                }
            }
            catch (Exception ex)
            {
                _isRegistered = false;
                System.Diagnostics.Debug.WriteLine($"心跳发送失败: {ex.Message}");
            }
        }

        public async void UpdateStatus(object statusData)
        {
            try
            {
                var content = new StringContent(
                    JsonSerializer.Serialize(statusData, StatusJsonOptions),
                    Encoding.UTF8,
                    "application/json");

                await _httpClient.PostAsync(
                    ClientApiPath("/status"), content);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"状态更新失败: {ex.Message}");
            }
        }

        public async Task<List<ClientMessage>> FetchMessagesAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync(ClientApiPath(""));
                if (!response.IsSuccessStatusCode)
                    return new List<ClientMessage>();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("clientMessages", out var msgsEl) ||
                    msgsEl.ValueKind != JsonValueKind.Array)
                    return new List<ClientMessage>();

                var messages = JsonSerializer.Deserialize<List<ClientMessage>>(msgsEl.GetRawText(), CommandJsonOptions)
                               ?? new List<ClientMessage>();
                messages.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
                return messages;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"获取消息列表失败: {ex.Message}");
                return new List<ClientMessage>();
            }
        }

        public async Task<bool> SendMessageToServerAsync(string text)
        {
            try
            {
                var payload = JsonSerializer.Serialize(new { text, direction = "from_client" });
                var content = new StringContent(payload, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(
                    ClientApiPath("/message"), content);

                if (response.IsSuccessStatusCode)
                    return true;

                var error = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"发送回复失败: HTTP {(int)response.StatusCode} {error}");
                _mainWindow.ShowNotification("消息发送失败", $"HTTP {(int)response.StatusCode}");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"发送回复失败: {ex.Message}");
                _mainWindow.ShowNotification("消息发送失败", ex.Message);
                return false;
            }
        }

        private async Task PollCommands()
        {
            while (_isRunning)
            {
                try
                {
                    if (_isRegistered)
                    {
                        var response = await _httpClient.GetAsync(
                            ClientApiPath("/commands"));

                        if (response.IsSuccessStatusCode)
                        {
                            var commandsJson = await response.Content.ReadAsStringAsync();
                            if (!string.IsNullOrEmpty(commandsJson) && commandsJson != "[]")
                            {
                                var commands = JsonSerializer.Deserialize<List<RemoteCommand>>(commandsJson, CommandJsonOptions);
                                if (commands != null && commands.Count > 0)
                                {
                                    ProcessCommands(commands);
                                }
                            }
                        }
                        else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            _isRegistered = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"命令轮询失败: {ex.Message}");
                }

                _pollRequested = false;
                var waited = 0;
                while (_isRunning && waited < PollIntervalMs && !_pollRequested)
                {
                    await Task.Delay(100);
                    waited += 100;
                }
            }
        }

        private void DispatchRemoteCommand(Action action)
        {
            _mainWindow.EnqueueRemoteCommand(action);
            _mainWindow.Dispatcher.BeginInvoke(new Action(() =>
            {
                _mainWindow.ProcessRemoteCommandQueue();
            }));
        }

        private void ProcessCommands(List<RemoteCommand> commands)
        {
            foreach (var command in commands)
            {
                if (command == null || string.IsNullOrWhiteSpace(command.Command))
                    continue;

                System.Diagnostics.Debug.WriteLine($"收到命令: {command.Command}");

                switch (command.Command)
                {
                    case "lock":
                        var lockMinutes = GetMinutesFromParameters(command.Parameters, 30);
                        DispatchRemoteCommand(() => _mainWindow.RemoteLock(lockMinutes));
                        break;

                    case "unlock":
                        var unlockMinutes = GetMinutesFromParameters(command.Parameters, 30);
                        DispatchRemoteCommand(() => _mainWindow.RemoteUnlock(unlockMinutes));
                        break;

                    case "update_config":
                        if (command.Parameters != null && TryGetParameterJson(command.Parameters, "config", out var configJson))
                        {
                            var remoteConfig = JsonSerializer.Deserialize<Dictionary<string, DaySchedule>>(configJson, CommandJsonOptions);
                            if (remoteConfig != null && remoteConfig.Count > 0)
                            {
                                DispatchRemoteCommand(() => _mainWindow.ApplyRemoteConfig(remoteConfig));
                                System.Diagnostics.Debug.WriteLine("配置已更新并立即生效");
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"配置解析失败或为空: {configJson}");
                            }
                        }
                        break;

                    case "update_app_policy":
                        if (command.Parameters != null && TryGetParameterJson(command.Parameters, "appPolicy", out var policyJson))
                        {
                            var policy = JsonSerializer.Deserialize<AppPolicy>(policyJson, CommandJsonOptions);
                            if (policy != null)
                            {
                                DispatchRemoteCommand(() => _mainWindow.ApplyAppPolicy(policy));
                                System.Diagnostics.Debug.WriteLine("应用权限已更新并立即生效");
                            }
                        }
                        break;

                    case "pause_timing":
                        DispatchRemoteCommand(() => _mainWindow.RemotePauseTiming());
                        break;

                    case "shutdown":
                        DispatchRemoteCommand(() =>
                        {
                            try
                            {
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                                    "shutdown", "/s /t 0")
                                {
                                    CreateNoWindow = true,
                                    UseShellExecute = false
                                });
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"关机失败: {ex.Message}");
                            }
                        });
                        break;

                    case "show_message":
                        var msgText = GetStringFromParameters(command.Parameters, "text");
                        var msgTitle = GetStringFromParameters(command.Parameters, "title") ?? "管理端消息";
                        if (!string.IsNullOrWhiteSpace(msgText))
                        {
                            var text = msgText;
                            var title = msgTitle;
                            DispatchRemoteCommand(() => _mainWindow.ShowMessageDialog(title, text));
                        }
                        break;

                    case "update":
                        var packageUrl = GetStringFromParameters(command.Parameters, "package_url");
                        var version = GetStringFromParameters(command.Parameters, "version");
                        if (!string.IsNullOrWhiteSpace(packageUrl))
                        {
                            DispatchRemoteCommand(() =>
                                _mainWindow.TriggerRemoteUpdate(packageUrl, version));
                        }
                        break;
                }
            }
        }

        private static bool TryGetParameterJson(Dictionary<string, object> parameters, string key, out string json)
        {
            json = null;
            if (parameters == null || !parameters.TryGetValue(key, out var value) || value == null)
                return false;

            if (value is JsonElement element)
                json = element.GetRawText();
            else
                json = value.ToString();

            return !string.IsNullOrWhiteSpace(json);
        }

        private string GetStringFromParameters(Dictionary<string, object> parameters, string key)
        {
            if (parameters == null || !parameters.TryGetValue(key, out var value))
                return null;

            if (value is JsonElement element)
                return element.GetString();

            return value?.ToString();
        }

        private int GetMinutesFromParameters(Dictionary<string, object> parameters, int defaultMinutes)
        {
            if (parameters == null || !parameters.TryGetValue("minutes", out var value))
                return defaultMinutes;

            if (value is JsonElement element)
            {
                if (element.TryGetInt32(out int minutes) && minutes > 0)
                    return minutes;
            }
            else if (int.TryParse(value?.ToString(), out int parsed) && parsed > 0)
            {
                return parsed;
            }

            return defaultMinutes;
        }

        private string GetLocalIpAddress()
        {
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        return ip.ToString();
                    }
                }
            }
            catch { }
            return "127.0.0.1";
        }
    }
}
