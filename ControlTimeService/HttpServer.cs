using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ControlTimeService
{
    public class ClientInfo
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("computerName")]
        public string ComputerName
        {
            get => Name;
            set => Name = value;
        }

        [JsonPropertyName("ip")]
        public string IpAddress { get; set; }
        public DateTime FirstSeen { get; set; }
        public DateTime LastHeartbeat { get; set; }
        public string Status { get; set; } = "Online";
        public string AppVersion { get; set; }
        public Dictionary<string, DaySchedule> Config { get; set; }
        public AppPolicy AppPolicy { get; set; }
        [JsonPropertyName("remainingSeconds")]
        public double RemainingSeconds { get; set; }
        [JsonPropertyName("totalUsageSecondsToday")]
        public double TotalUsageSecondsToday { get; set; }

        [JsonIgnore]
        public double TotalUsageHoursToday => Math.Round(TotalUsageSecondsToday / 3600.0, 1);

        [JsonIgnore]
        public string RemainingTimeDisplay => FormatRemainingTime(RemainingSeconds);

        public static string FormatRemainingTime(double seconds)
        {
            if (seconds <= 0 || seconds > 86400 * 7)
                return "-";
            var ts = TimeSpan.FromSeconds(Math.Min(seconds, 86400));
            return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }

        public bool IsResting { get; set; }
        public bool IsShutdownMode { get; set; }
        public bool IsTimingPaused { get; set; }
        public List<RemoteCommand> PendingCommands { get; set; } = new List<RemoteCommand>();
        public List<ClientMessage> ClientMessages { get; set; } = new List<ClientMessage>();
    }

    public class ClientMessage
    {
        [JsonPropertyName("text")]
        public string Text { get; set; }
        [JsonPropertyName("direction")]
        public string Direction { get; set; } // "to_client" or "from_client"
        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    public class RemoteCommand
    {
        [JsonPropertyName("command")]
        public string Command { get; set; }  // "lock", "unlock", "shutdown", "update_config"

        [JsonPropertyName("parameters")]
        public Dictionary<string, object> Parameters { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    public class HttpServer
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private HttpListener _listener;
        private Dictionary<string, ClientInfo> _clients = new Dictionary<string, ClientInfo>();
        private readonly ClientRegistryManager _registry = new ClientRegistryManager();
        private int _port;
        public string PublicBaseUrl { get; set; }
        public string WebAdminPassword { get; set; } = "lnbxSoftLizhenNiping";
        public Func<string> ResolveUpdateVersion { get; set; }
        public Func<string> ResolveUpdatePackagePath { get; set; }

        /// <summary>客户端发来新消息时触发（direction = from_client）</summary>
        public event Action<string, ClientMessage> ClientMessageReceived;

        public Dictionary<string, ClientInfo> Clients => _clients;
        public int Port => _port;
        public int RequestedPort { get; private set; }
        public bool IsRunning => _listener != null && _listener.IsListening;

        public HttpServer(int port = 9528)
        {
            _port = port;
            RequestedPort = port;
        }

        public void Start()
        {
            Stop();

            RequestedPort = _port;
            var portsToTry = BuildPortCandidates(_port);
            Exception lastException = null;
            bool accessDenied = false;
            bool addressInUse = false;

            foreach (int port in portsToTry)
            {
                foreach (var prefix in BuildPrefixCandidates(port))
                {
                    HttpListener listener = null;
                    try
                    {
                        listener = new HttpListener();
                        listener.Prefixes.Add(prefix);
                        listener.Start();

                        _listener = listener;
                        _port = port;
                        _bindPrefix = prefix;

                        System.Diagnostics.Debug.WriteLine($"HTTP 服务器已启动: {prefix}");
                        Task.Run(() => ListenForRequests());
                        return;
                    }
                    catch (HttpListenerException ex)
                    {
                        lastException = ex;
                        System.Diagnostics.Debug.WriteLine($"无法绑定 {prefix}: ({ex.ErrorCode}) {ex.Message}");

                        if (ex.ErrorCode == 5)
                            accessDenied = true;
                        else if (ex.ErrorCode == 32 || ex.ErrorCode == 183 || ex.ErrorCode == 10048)
                            addressInUse = true;
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        System.Diagnostics.Debug.WriteLine($"启动 HTTP 服务器失败 ({prefix}): {ex.Message}");
                    }
                    finally
                    {
                        if (!ReferenceEquals(listener, _listener))
                        {
                            try
                            {
                                if (listener != null && listener.IsListening)
                                    listener.Stop();
                                listener?.Close();
                            }
                            catch { }
                        }
                    }
                }
            }

            if (lastException != null)
            {
                if (accessDenied)
                {
                    throw new UnauthorizedAccessException(
                        "无法启动 HTTP 服务器：权限不足。\n\n" +
                        "请右键 ControlCenter →「以管理员身份运行」。\n\n" +
                        "或在本机管理员 PowerShell 中执行一次（将 9528 换成你要用的端口）：\n" +
                        "netsh http add urlacl url=http://+:9528/ user=Everyone",
                        lastException);
                }

                throw new InvalidOperationException(
                    BuildPortFailureMessage(portsToTry, addressInUse),
                    lastException);
            }
        }

        private string _bindPrefix;

        private static int[] BuildPortCandidates(int preferredPort)
        {
            var ports = new List<int>();
            void Add(int port)
            {
                if (port > 0 && port <= 65535 && !ports.Contains(port))
                    ports.Add(port);
            }

            Add(preferredPort);
            for (int i = 1; i <= 5; i++)
                Add(preferredPort + i);

            return ports.ToArray();
        }

        private static IEnumerable<string> BuildPrefixCandidates(int port)
        {
            yield return $"http://+:{port}/";
            yield return $"http://*:{port}/";
        }

        private static string BuildPortFailureMessage(int[] portsTried, bool addressInUse)
        {
            var sample = string.Join(", ", portsTried.Take(8)) + (portsTried.Length > 8 ? "..." : "");
            var reason = addressInUse
                ? "多个端口已被其他程序占用"
                : "无法绑定到可用端口";

            return
                $"无法启动 HTTP 服务器。{reason}。\n\n" +
                $"已尝试端口（部分）：{sample}\n\n" +
                "请按顺序尝试：\n" +
                "1. 关闭多余的 ControlCenter 窗口（任务管理器结束 ControlCenter.exe）\n" +
                "2. 右键 ControlCenter →「以管理员身份运行」\n" +
                "3. 在管理端修改「监听端口」为其他数字（如 19528）后点「启动服务器」\n" +
                "4. 查看占用：netstat -ano | findstr \"9528\"\n\n" +
                "若仅需本机调试，可在 server_config.json 中将 listen_port 改为 19528。";
        }

        public void Stop()
        {
            if (_listener != null && _listener.IsListening)
            {
                _listener.Stop();
                _listener.Close();
            }
            _listener = null;
        }

        private async Task ListenForRequests()
        {
            while (_listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    HandleRequest(context);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"HTTP 请求处理错误: {ex.Message}");
                }
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                var path = request.Url.AbsolutePath;

                if (path == "/api/clients/register" && request.HttpMethod == "POST")
                {
                    HandleRegister(request, response);
                }
                else if (path == "/api/clients/heartbeat" && request.HttpMethod == "POST")
                {
                    HandleHeartbeat(request, response);
                }
                else if (path.StartsWith("/api/clients/") && path.EndsWith("/commands") && request.HttpMethod == "GET")
                {
                    HandleGetCommands(request, response);
                }
                else if (path.StartsWith("/api/clients/") && path.EndsWith("/status") && request.HttpMethod == "POST")
                {
                    HandleUpdateStatus(request, response);
                }
                else if (path == "/api/clients" && request.HttpMethod == "GET")
                {
                    HandleGetClients(response);
                }
                else if (path.StartsWith("/api/clients/") && request.HttpMethod == "GET" 
                         && path.Split('/').Length == 4)
                {
                    HandleGetClient(request, response);
                }
                else if (path.StartsWith("/api/clients/") && path.EndsWith("/lock") && request.HttpMethod == "POST")
                {
                    HandleLockClient(request, response);
                }
                else if (path.StartsWith("/api/clients/") && path.EndsWith("/unlock") && request.HttpMethod == "POST")
                {
                    HandleUnlockClient(request, response);
                }
                else if (path.StartsWith("/api/clients/") && path.EndsWith("/config") && request.HttpMethod == "POST")
                {
                    HandleUpdateConfig(request, response);
                }
                else if (path.StartsWith("/api/clients/") && path.EndsWith("/app-policy") && request.HttpMethod == "POST")
                {
                    HandleUpdateAppPolicy(request, response);
                }
                else if (path.StartsWith("/api/clients/") && path.EndsWith("/pause") && request.HttpMethod == "POST")
                {
                    HandlePauseClient(request, response);
                }
                else if (path.StartsWith("/api/clients/") && path.EndsWith("/shutdown") && request.HttpMethod == "POST")
                {
                    HandleShutdownClient(request, response);
                }
                else if (path.StartsWith("/api/clients/") && path.EndsWith("/message") && request.HttpMethod == "POST")
                {
                    HandleSendMessage(request, response);
                }
                else if (path.StartsWith("/api/clients/") && path.EndsWith("/update") && request.HttpMethod == "POST")
                {
                    HandleUpdateClient(request, response);
                }
                else if (path == "/api/updates/manifest.json" && request.HttpMethod == "GET")
                {
                    HandleGetUpdateManifest(request, response);
                }
                else if (path.StartsWith("/api/updates/") && request.HttpMethod == "GET")
                {
                    HandleGetUpdatePackage(request, response);
                }
                else if (path.StartsWith("/web") || path == "/" || path.StartsWith("/api/web/"))
                {
                    HandleWebRequest(request, response);
                }
                else
                {
                    SendResponse(response, 404, "Not Found");
                }
            }
            catch (Exception ex)
            {
                SendResponse(response, 500, $"Internal Server Error: {ex.Message}");
            }
        }

        private void HandleRegister(HttpListenerRequest request, HttpListenerResponse response)
        {
            var body = ReadRequestBody(request);
            var clientData = JsonSerializer.Deserialize<ClientInfo>(body, JsonOptions);

            if (clientData != null && !string.IsNullOrEmpty(clientData.Id))
            {
                ClientInfo client;
                if (_clients.TryGetValue(clientData.Id, out var existing))
                {
                    client = existing;
                    client.Name = clientData.Name ?? client.Name;
                    client.IpAddress = clientData.IpAddress ?? client.IpAddress;
                    client.AppVersion = clientData.AppVersion ?? client.AppVersion;
                }
                else
                {
                    client = clientData;
                    client.PendingCommands ??= new List<RemoteCommand>();
                    _clients[clientData.Id] = client;
                }

                client.LastHeartbeat = DateTime.Now;
                client.Status = "Online";

                var record = _registry.UpsertFromRegistration(client);
                if (record != null)
                {
                    client.FirstSeen = record.FirstSeen;
                    if (!string.IsNullOrWhiteSpace(record.ComputerName))
                        client.Name = record.ComputerName;

                    // 恢复消息历史
                    if (record.ClientMessages != null && record.ClientMessages.Count > 0)
                        client.ClientMessages = record.ClientMessages;

                    // 注册表有配置时以注册表为准（管理端下发的配置），并推送给客户端
                    if (record.Config != null && record.Config.Count > 0)
                    {
                        client.Config = record.Config;
                        if (clientData.Config == null || !ConfigsEqual(clientData.Config, record.Config))
                            QueueConfigCommand(client, record.Config);
                    }
                    else if (clientData.Config != null && clientData.Config.Count > 0)
                    {
                        client.Config = clientData.Config;
                        _registry.SaveConfig(client.Id, clientData.Config);
                    }

                    if (record.AppPolicy != null)
                        client.AppPolicy ??= record.AppPolicy;
                }

                System.Diagnostics.Debug.WriteLine($"客户端注册: {client.Name} ({client.IpAddress})");
                SendResponse(response, 200, "OK");
            }
            else
            {
                SendResponse(response, 400, "Invalid client data");
            }
        }

        private void HandleHeartbeat(HttpListenerRequest request, HttpListenerResponse response)
        {
            var body = ReadRequestBody(request);
            var heartbeatData = JsonSerializer.Deserialize<Dictionary<string, string>>(body, JsonOptions);

            if (heartbeatData != null && heartbeatData.TryGetValue("id", out var clientId) && !string.IsNullOrEmpty(clientId))
            {
                if (_clients.ContainsKey(clientId))
                {
                    _clients[clientId].LastHeartbeat = DateTime.Now;
                    _clients[clientId].Status = "Online";

                    if (heartbeatData.TryGetValue("name", out var computerName) && !string.IsNullOrWhiteSpace(computerName))
                    {
                        _clients[clientId].Name = computerName;
                        _registry.UpdateComputerName(clientId, computerName, _clients[clientId].IpAddress);
                    }
                    else
                    {
                        _registry.UpdateLastSeen(clientId);
                    }
                }
                else
                {
                    SendResponse(response, 404, "Client not registered");
                    return;
                }
            }

            SendResponse(response, 200, "OK");
        }

        private void HandleGetCommands(HttpListenerRequest request, HttpListenerResponse response)
        {
            var pathParts = request.Url.AbsolutePath.Split('/');
            var clientId = Uri.UnescapeDataString(pathParts[pathParts.Length - 2]);

            if (_clients.ContainsKey(clientId))
            {
                var client = _clients[clientId];
                client.PendingCommands ??= new List<RemoteCommand>();
                var commands = client.PendingCommands;
                var json = JsonSerializer.Serialize(commands, JsonOptions);

                // 返回后清空命令列表
                client.PendingCommands.Clear();
                
                SendJsonResponse(response, 200, json);
            }
            else
            {
                SendResponse(response, 404, "Client not found");
            }
        }

        private void HandleUpdateStatus(HttpListenerRequest request, HttpListenerResponse response)
        {
            var pathParts = request.Url.AbsolutePath.Split('/');
            var clientId = Uri.UnescapeDataString(pathParts[pathParts.Length - 2]);

            if (_clients.ContainsKey(clientId))
            {
                var body = ReadRequestBody(request);
                var statusData = JsonSerializer.Deserialize<Dictionary<string, object>>(body);

                if (statusData != null)
                {
                    var client = _clients[clientId];
                    
                    if (statusData.TryGetValue("isResting", out var isRestingVal))
                        client.IsResting = ReadJsonBool(isRestingVal);
                    
                    if (statusData.TryGetValue("isShutdownMode", out var isShutdownVal))
                        client.IsShutdownMode = ReadJsonBool(isShutdownVal);

                    if (statusData.TryGetValue("isTimingPaused", out var isPausedVal))
                        client.IsTimingPaused = ReadJsonBool(isPausedVal);
                    
                    if (statusData.TryGetValue("remainingSeconds", out var remainingVal))
                        client.RemainingSeconds = ReadJsonDouble(remainingVal);

                    if (statusData.TryGetValue("totalUsageSecondsToday", out var usageVal))
                        client.TotalUsageSecondsToday = ReadJsonDouble(usageVal);

                    // 配置与策略以注册表（管理端下发）为准，不在状态上报时覆盖
                    client.Status = client.IsTimingPaused ? "Paused" : (client.IsResting ? "Locked" : "Using");
                }

                SendResponse(response, 200, "OK");
            }
            else
            {
                SendResponse(response, 404, "Client not found");
            }
        }

        private void HandleGetClients(HttpListenerResponse response)
        {
            var result = new List<ClientInfo>();
            var onlineIds = new HashSet<string>(_clients.Keys);

            foreach (var client in _clients.Values)
            {
                result.Add(CloneClientForList(client));
            }

            foreach (var record in _registry.GetAllRecords())
            {
                if (onlineIds.Contains(record.Id))
                    continue;

                result.Add(record.ToClientInfo(false));
            }

            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            var json = JsonSerializer.Serialize(result, JsonOptions);
            SendJsonResponse(response, 200, json);
        }

        private void HandleGetClient(HttpListenerRequest request, HttpListenerResponse response)
        {
            var pathParts = request.Url.AbsolutePath.Split('/');
            var clientId = Uri.UnescapeDataString(pathParts[pathParts.Length - 1]);

            if (_clients.TryGetValue(clientId, out var client))
            {
                var fresh = CloneClientForList(client);
                var json = JsonSerializer.Serialize(fresh, JsonOptions);
                SendJsonResponse(response, 200, json);
            }
            else
            {
                var record = _registry.Get(clientId);
                if (record != null)
                {
                    var info = record.ToClientInfo(false);
                    info.ClientMessages = SortMessagesNewestFirst(info.ClientMessages);
                    var json = JsonSerializer.Serialize(info, JsonOptions);
                    SendJsonResponse(response, 200, json);
                }
                else
                {
                    SendResponse(response, 404, "Client not found");
                }
            }
        }

        private ClientInfo CloneClientForList(ClientInfo client)
        {
            var record = _registry.Get(client.Id);
            var messages = client.ClientMessages ?? record?.ClientMessages;
            return new ClientInfo
            {
                Id = client.Id,
                Name = client.Name,
                IpAddress = client.IpAddress,
                FirstSeen = client.FirstSeen != default ? client.FirstSeen : record?.FirstSeen ?? default,
                LastHeartbeat = client.LastHeartbeat,
                Status = client.Status,
                AppVersion = client.AppVersion,
                Config = record?.Config ?? client.Config,
                AppPolicy = record?.AppPolicy ?? client.AppPolicy,
                RemainingSeconds = client.RemainingSeconds,
                TotalUsageSecondsToday = client.TotalUsageSecondsToday,
                IsResting = client.IsResting,
                IsShutdownMode = client.IsShutdownMode,
                IsTimingPaused = client.IsTimingPaused,
                ClientMessages = SortMessagesNewestFirst(messages)
            };
        }

        private static List<ClientMessage> SortMessagesNewestFirst(List<ClientMessage> messages)
        {
            if (messages == null || messages.Count == 0)
                return messages ?? new List<ClientMessage>();

            var sorted = new List<ClientMessage>(messages);
            sorted.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
            return sorted;
        }

        private void EnqueueCommand(ClientInfo client, RemoteCommand command)
        {
            client.PendingCommands ??= new List<RemoteCommand>();
            client.PendingCommands.Add(command);
        }

        private void QueueConfigCommand(ClientInfo client, Dictionary<string, DaySchedule> config)
        {
            client.PendingCommands ??= new List<RemoteCommand>();
            client.PendingCommands.RemoveAll(c =>
                string.Equals(c.Command, "update_config", StringComparison.OrdinalIgnoreCase));

            EnqueueCommand(client, new RemoteCommand
            {
                Command = "update_config",
                Parameters = new Dictionary<string, object> { { "config", config } }
            });
        }

        private void QueueAppPolicyCommand(ClientInfo client, AppPolicy policy)
        {
            EnqueueCommand(client, new RemoteCommand
            {
                Command = "update_app_policy",
                Parameters = new Dictionary<string, object> { { "appPolicy", policy } }
            });
        }

        private void HandleLockClient(HttpListenerRequest request, HttpListenerResponse response)
        {
            var pathParts = request.Url.AbsolutePath.Split('/');
            var clientId = pathParts[pathParts.Length - 2];

            if (_clients.ContainsKey(clientId))
            {
                var minutes = ParseMinutesFromBody(request, 30);
                EnqueueCommand(_clients[clientId], new RemoteCommand
                {
                    Command = "lock",
                    Parameters = new Dictionary<string, object> { { "minutes", minutes } }
                });

                SendResponse(response, 200, "Lock command sent");
            }
            else
            {
                SendResponse(response, 404, "Client not found");
            }
        }

        private void HandleUnlockClient(HttpListenerRequest request, HttpListenerResponse response)
        {
            var pathParts = request.Url.AbsolutePath.Split('/');
            var clientId = pathParts[pathParts.Length - 2];

            if (_clients.ContainsKey(clientId))
            {
                var minutes = ParseMinutesFromBody(request, 30);
                EnqueueCommand(_clients[clientId], new RemoteCommand
                {
                    Command = "unlock",
                    Parameters = new Dictionary<string, object> { { "minutes", minutes } }
                });

                SendResponse(response, 200, "Unlock command sent");
            }
            else
            {
                SendResponse(response, 404, "Client not found");
            }
        }

        private int ParseMinutesFromBody(HttpListenerRequest request, int defaultMinutes)
        {
            try
            {
                var body = ReadRequestBody(request);
                if (string.IsNullOrWhiteSpace(body))
                    return defaultMinutes;

                var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body, JsonOptions);
                if (data != null && data.TryGetValue("minutes", out var minutesElement))
                {
                    if (minutesElement.TryGetInt32(out int minutes) && minutes > 0)
                        return minutes;
                }
            }
            catch { }
            return defaultMinutes;
        }

        private void HandleUpdateConfig(HttpListenerRequest request, HttpListenerResponse response)
        {
            var pathParts = request.Url.AbsolutePath.Split('/');
            var clientId = Uri.UnescapeDataString(pathParts[pathParts.Length - 2]);

            var body = ReadRequestBody(request);
            var configData = JsonSerializer.Deserialize<Dictionary<string, DaySchedule>>(body, JsonOptions);

            if (configData == null)
            {
                SendResponse(response, 400, "Invalid config data");
                return;
            }

            var existingConfig = _registry.Get(clientId)?.Config;
            if (existingConfig == null && _clients.TryGetValue(clientId, out var existingClient))
                existingConfig = existingClient.Config;

            var mergedConfig = TimeConfigManager.MergeConfig(existingConfig, configData);
            _registry.SaveConfig(clientId, mergedConfig);

            if (_clients.ContainsKey(clientId))
            {
                _clients[clientId].Config = mergedConfig;
                QueueConfigCommand(_clients[clientId], mergedConfig);
                SendResponse(response, 200, "Config update command sent");
            }
            else
            {
                SendResponse(response, 200, "Config saved; will apply when client connects");
            }
        }

        private void HandleUpdateAppPolicy(HttpListenerRequest request, HttpListenerResponse response)
        {
            var pathParts = request.Url.AbsolutePath.Split('/');
            var clientId = pathParts[pathParts.Length - 2];

            var body = ReadRequestBody(request);
            var policy = JsonSerializer.Deserialize<AppPolicy>(body, JsonOptions);

            if (policy == null)
            {
                SendResponse(response, 400, "Invalid app policy data");
                return;
            }

            // 向后兼容：将全局 AppPolicy 合并到每天配置中
            if (_clients.TryGetValue(clientId, out var clientInfo))
            {
                if (clientInfo.Config != null)
                {
                    foreach (var kvp in clientInfo.Config)
                    {
                        kvp.Value.AllowVideo = policy.AllowVideo;
                        kvp.Value.AllowWeChatMiniGames = policy.AllowWeChatMiniGames;
                        kvp.Value.AllowMaoxiang = policy.AllowMaoxiang;
                        kvp.Value.AllowDouyin = policy.AllowDouyin;
                        kvp.Value.AllowFanqieNovel = policy.AllowFanqieNovel;
                        kvp.Value.AllowTencentAppStore = policy.AllowTencentAppStore;
                        kvp.Value.AllowOtherGames = policy.AllowOtherGames;
                    }
                    _registry.SaveConfig(clientId, clientInfo.Config);
                    QueueConfigCommand(clientInfo, clientInfo.Config);
                    SendResponse(response, 200, "App policy merged into config; update command sent");
                }
                else
                {
                    clientInfo.AppPolicy = policy;
                    _registry.SaveAppPolicy(clientId, policy);
                    QueueAppPolicyCommand(clientInfo, policy);
                    SendResponse(response, 200, "App policy saved; config not yet available");
                }
            }
            else
            {
                _registry.SaveAppPolicy(clientId, policy);
                SendResponse(response, 200, "App policy saved; will apply when client connects");
            }
        }

        private void HandlePauseClient(HttpListenerRequest request, HttpListenerResponse response)
        {
            var pathParts = request.Url.AbsolutePath.Split('/');
            var clientId = pathParts[pathParts.Length - 2];

            if (_clients.ContainsKey(clientId))
            {
                EnqueueCommand(_clients[clientId], new RemoteCommand
                {
                    Command = "pause_timing"
                });

                SendResponse(response, 200, "Pause timing command sent");
            }
            else
            {
                SendResponse(response, 404, "Client not found");
            }
        }

        private void HandleSendMessage(HttpListenerRequest request, HttpListenerResponse response)
        {
            var pathParts = request.Url.AbsolutePath.Split('/');
            var clientId = Uri.UnescapeDataString(pathParts[pathParts.Length - 2]);

            var record = _registry.Get(clientId);
            if (record == null && !_clients.ContainsKey(clientId))
            {
                SendResponse(response, 404, "Client not found");
                return;
            }

            var body = ReadRequestBody(request);
            var msgData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body, JsonOptions);
            var text = msgData != null && msgData.TryGetValue("text", out var textEl)
                ? textEl.GetString()
                : "";
            if (string.IsNullOrWhiteSpace(text))
            {
                SendResponse(response, 400, "Message text is required");
                return;
            }

            var direction = "to_client";
            if (msgData != null && msgData.TryGetValue("direction", out var dirEl))
                direction = dirEl.GetString() ?? "to_client";
            var isFromClient = string.Equals(direction, "from_client", StringComparison.OrdinalIgnoreCase);

            if (isFromClient && !_clients.ContainsKey(clientId))
            {
                SendResponse(response, 404, "Client not registered");
                return;
            }

            var message = new ClientMessage
            {
                Text = text,
                Direction = isFromClient ? "from_client" : "to_client",
                Timestamp = DateTime.Now
            };

            List<ClientMessage> messages;
            if (_clients.TryGetValue(clientId, out var client))
            {
                client.ClientMessages ??= new List<ClientMessage>();
                client.ClientMessages.Add(message);
                messages = client.ClientMessages;
            }
            else
            {
                messages = record?.ClientMessages ?? new List<ClientMessage>();
                messages.Add(message);
            }

            _registry.SaveMessages(clientId, messages);

            if (isFromClient)
            {
                ClientMessageReceived?.Invoke(clientId, message);
                SendResponse(response, 200, "Reply received");
                return;
            }

            if (_clients.TryGetValue(clientId, out client))
            {
                EnqueueCommand(client, new RemoteCommand
                {
                    Command = "show_message",
                    Parameters = new Dictionary<string, object>
                    {
                        { "text", text },
                        { "title", "来自管理端的消息" }
                    }
                });
                SendResponse(response, 200, "Message sent");
            }
            else
            {
                SendResponse(response, 200, "Message saved; will notify when client connects");
            }
        }

        private void HandleShutdownClient(HttpListenerRequest request, HttpListenerResponse response)
        {
            var pathParts = request.Url.AbsolutePath.Split('/');
            var clientId = pathParts[pathParts.Length - 2];

            if (_clients.ContainsKey(clientId))
            {
                EnqueueCommand(_clients[clientId], new RemoteCommand
                {
                    Command = "shutdown"
                });

                SendResponse(response, 200, "Shutdown command sent");
            }
            else
            {
                SendResponse(response, 404, "Client not found");
            }
        }

        public int PushUpdateToAllClients(string packageUrl, string version)
        {
            if (string.IsNullOrWhiteSpace(packageUrl))
                return 0;

            var count = 0;
            foreach (var client in _clients.Values)
            {
                if (QueueUpdateCommand(client, packageUrl, version))
                    count++;
            }

            return count;
        }

        public bool PushUpdateToClient(string clientId, string packageUrl, string version)
        {
            if (string.IsNullOrWhiteSpace(packageUrl) || !_clients.TryGetValue(clientId, out var client))
                return false;

            return QueueUpdateCommand(client, packageUrl, version);
        }

        private bool QueueUpdateCommand(ClientInfo client, string packageUrl, string version)
        {
            var parameters = new Dictionary<string, object>
            {
                { "package_url", packageUrl }
            };

            if (!string.IsNullOrWhiteSpace(version))
                parameters["version"] = version;

            EnqueueCommand(client, new RemoteCommand
            {
                Command = "update",
                Parameters = parameters
            });

            return true;
        }

        private void HandleGetUpdateManifest(HttpListenerRequest request, HttpListenerResponse response)
        {
            var version = ResolveUpdateVersion?.Invoke() ?? "1.0.0";
            var packageUrl = BuildPublicUpdatePackageUrl(request);

            if (string.IsNullOrWhiteSpace(packageUrl))
            {
                SendResponse(response, 404, "Update package not configured");
                return;
            }

            var manifest = new
            {
                version,
                package_url = packageUrl,
                description = "ControlTimeService update"
            };

            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            SendJsonResponse(response, 200, json);
        }

        private void HandleGetUpdatePackage(HttpListenerRequest request, HttpListenerResponse response)
        {
            var fileName = Uri.UnescapeDataString(request.Url.AbsolutePath.Substring("/api/updates/".Length));
            if (string.IsNullOrWhiteSpace(fileName) ||
                fileName.Contains("..", StringComparison.Ordinal) ||
                fileName.Contains('/', StringComparison.Ordinal) ||
                fileName.Contains('\\', StringComparison.Ordinal))
            {
                SendResponse(response, 400, "Invalid file name");
                return;
            }

            var configuredPath = ResolveUpdatePackagePath?.Invoke();
            var localPath = !string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath)
                ? configuredPath
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "updates", fileName);

            if (!File.Exists(localPath))
            {
                SendResponse(response, 404, "Package not found");
                return;
            }

            var bytes = File.ReadAllBytes(localPath);
            response.StatusCode = 200;
            response.ContentType = "application/zip";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.OutputStream.Close();
        }

        private string BuildPublicUpdatePackageUrl(HttpListenerRequest request = null)
        {
            var configuredPath = ResolveUpdatePackagePath?.Invoke();
            if (string.IsNullOrWhiteSpace(configuredPath))
                return null;

            var baseUrl = ResolveRequestBaseUrl(request);
            if (string.IsNullOrWhiteSpace(baseUrl))
                return null;

            var fileName = Path.GetFileName(configuredPath);
            return $"{baseUrl.TrimEnd('/')}/api/updates/{Uri.EscapeDataString(fileName)}";
        }

        private string ResolveRequestBaseUrl(HttpListenerRequest request)
        {
            if (request != null)
            {
                var host = request.Headers["Host"];
                if (!string.IsNullOrWhiteSpace(host))
                    return $"http://{host.Trim()}";

                if (request.Url != null)
                    return $"{request.Url.Scheme}://{request.Url.Authority}";
            }

            return PublicBaseUrl;
        }

        private static bool ConfigsEqual(Dictionary<string, DaySchedule> a, Dictionary<string, DaySchedule> b)
        {
            if (a == null || b == null)
                return a == b;

            if (a.Count != b.Count)
                return false;

            foreach (var kvp in a)
            {
                if (!b.TryGetValue(kvp.Key, out var other))
                    return false;

                var left = JsonSerializer.Serialize(kvp.Value, JsonOptions);
                var right = JsonSerializer.Serialize(other, JsonOptions);
                if (!string.Equals(left, right, StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        private static double ReadJsonDouble(object value)
        {
            if (value is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Number)
                    return element.GetDouble();
                if (element.ValueKind == JsonValueKind.String &&
                    double.TryParse(element.GetString(), out var parsed))
                    return parsed;
            }

            return Convert.ToDouble(value);
        }

        private static bool ReadJsonBool(object value)
        {
            if (value is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.True) return true;
                if (element.ValueKind == JsonValueKind.False) return false;
                if (element.ValueKind == JsonValueKind.String &&
                    bool.TryParse(element.GetString(), out var parsed))
                    return parsed;
            }

            return Convert.ToBoolean(value);
        }

        private void HandleUpdateClient(HttpListenerRequest request, HttpListenerResponse response)
        {
            var pathParts = request.Url.AbsolutePath.Split('/');
            var clientId = pathParts[pathParts.Length - 2];

            if (_clients.ContainsKey(clientId))
            {
                var body = ReadRequestBody(request);
                var updateData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body, JsonOptions);

                if (updateData == null ||
                    !updateData.TryGetValue("package_url", out var packageUrlElement) ||
                    string.IsNullOrWhiteSpace(packageUrlElement.GetString()))
                {
                    SendResponse(response, 400, "package_url is required");
                    return;
                }

                QueueUpdateCommand(
                    _clients[clientId],
                    packageUrlElement.GetString()!,
                    updateData.TryGetValue("version", out var versionElement) ? versionElement.GetString() : null);

                SendResponse(response, 200, "Update command sent");
            }
            else
            {
                SendResponse(response, 404, "Client not found");
            }
        }

        private void HandleWebRequest(HttpListenerRequest request, HttpListenerResponse response)
        {
            // API 路由：/api/web/login（登录验证）
            if (request.HttpMethod == "POST" && request.Url.AbsolutePath == "/api/web/login")
            {
                var body = ReadRequestBody(request);
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(body, JsonOptions);
                var password = data?.GetValueOrDefault("password", "");
                var isValid = string.Equals(password, WebAdminPassword, StringComparison.Ordinal);
                var result = JsonSerializer.Serialize(new { success = isValid });
                SendJsonResponse(response, isValid ? 200 : 401, result);
                return;
            }

            // /api/web/* 的其他路径→404（预留未来 API）
            if (request.Url.AbsolutePath.StartsWith("/api/web/"))
            {
                SendResponse(response, 404, "Not Found");
                return;
            }

            // 所有 /web/* 和 / 路径返回 SPA 页面
            var html = GetWebPageHtml();
            response.StatusCode = 200;
            response.ContentType = "text/html; charset=utf-8";
            var buffer = Encoding.UTF8.GetBytes(html);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }

        private static string GetWebPageHtml()
        {
            // 优先读取同目录下的 web_index.html（便于独立维护与美化），失败则回退到内嵌精简版
            try
            {
                var webPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web_index.html");
                if (File.Exists(webPath))
                    return File.ReadAllText(webPath);
            }
            catch { }
            return FallbackWebPageHtml();
        }

        private static string FallbackWebPageHtml()
        {
            return @"<!DOCTYPE html>
<html lang=""zh-CN""><head><meta charset=""UTF-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""><title>时间控制管理</title>
<style>body{font-family:sans-serif;margin:40px;text-align:center}h1{color:#007ACC}p{color:#666}</style></head>
<body><h1>时间控制管理</h1><p>Web 页面文件缺失，请确认 web_index.html 存在于程序目录。</p></body></html>";
        }

                private string ReadRequestBody(HttpListenerRequest request)
        {
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            {
                return reader.ReadToEnd();
            }
        }

        private void SendResponse(HttpListenerResponse response, int statusCode, string message)
        {
            response.StatusCode = statusCode;
            var buffer = Encoding.UTF8.GetBytes(message);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }

        private void SendJsonResponse(HttpListenerResponse response, int statusCode, string json)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            var buffer = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }
    }
}
