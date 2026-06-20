using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ControlTimeService;

namespace ControlCenter
{
    public partial class MainWindow : Window
    {
        private HttpServer _httpServer;
        private HttpClient _httpClient;
        private string _serverUrl = "http://localhost:9528";
        private System.Windows.Threading.DispatcherTimer _refreshTimer;
        private System.Windows.Threading.DispatcherTimer _messageRefreshTimer;
        private UpdateServerConfig _updateConfig;
        private ServerConfig _serverConfig;
        private string _clientConnectUrl = "";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        public MainWindow()
        {
            InitializeComponent();
            _httpClient = new HttpClient();
            _updateConfig = UpdateServerConfig.Load();
            _serverConfig = ServerConfig.Load();
            LoadUpdateConfigToUi();
            ListenPortText.Text = _serverConfig.ListenPort.ToString();
            
            // 自动启动服务器
            StartServer();
            
            // 定时刷新客户端列表
            _refreshTimer = new System.Windows.Threading.DispatcherTimer();
            _refreshTimer.Interval = TimeSpan.FromSeconds(10);
            _refreshTimer.Tick += (s, e) => RefreshClientList();
            _refreshTimer.Start();
        }

        private void StartServer()
        {
            HttpServer server = null;
            var requestedPort = 0;

            try
            {
                _httpServer?.Stop();
                _httpServer = null;

                if (!int.TryParse(ListenPortText.Text.Trim(), out requestedPort) || requestedPort <= 0 || requestedPort > 65535)
                {
                    MessageBox.Show("请输入有效的监听端口（1-65535）", "端口无效",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    UpdateServerUi(false);
                    return;
                }

                _serverConfig.ListenPort = requestedPort;
                _serverConfig.Save();

                server = new HttpServer(requestedPort);
                server.WebAdminPassword = _serverConfig.WebAdminPassword;
                server.Start();
                _httpServer = server;
                _httpServer.ClientMessageReceived += OnClientMessageReceived;

                _serverUrl = $"http://localhost:{_httpServer.Port}";
                ListenPortText.Text = _httpServer.Port.ToString();

                UpdateServerUi(true);

                if (_httpServer.Port != requestedPort)
                {
                    MessageBox.Show(
                        $"端口 {requestedPort} 被占用，服务器已改用端口 {_httpServer.Port}。\n\n" +
                        $"请把客户端 control_config.json 中的 server_url 改为：\n{_clientConnectUrl}",
                        "端口已自动切换",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                RefreshClientList();
            }
            catch (UnauthorizedAccessException ex)
            {
                server?.Stop();
                _httpServer = null;
                UpdateServerUi(false);

                string message = $"权限不足：{ex.Message}\n\n" +
                    "解决方法：\n" +
                    "1. 右键点击程序 -> '以管理员身份运行'\n" +
                    "2. 或者在防火墙设置中允许此程序\n" +
                    "3. 关闭占用端口的其他程序";
                MessageBox.Show(message, "启动失败 - 需要管理员权限",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (InvalidOperationException ex)
            {
                server?.Stop();
                _httpServer = null;
                UpdateServerUi(false);
                MessageBox.Show(ex.Message, "启动失败 - 端口不可用",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                server?.Stop();
                _httpServer = null;
                UpdateServerUi(false);

                string message = $"启动服务器失败: {ex.Message}\n\n" +
                    $"详细信息: {ex.InnerException?.Message}\n\n" +
                    "可能原因：\n" +
                    "1. 端口被其他程序占用\n" +
                    "2. 没有管理员权限\n" +
                    "3. 防火墙阻止了端口监听\n\n" +
                    "建议：以管理员身份运行此程序";
                MessageBox.Show(message, "启动失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateServerUi(bool isRunning)
        {
            if (isRunning && _httpServer != null)
            {
                var lanIp = GetLocalIpAddress();
                _clientConnectUrl = $"http://{lanIp}:{_httpServer.Port}";
                var localUrl = $"http://localhost:{_httpServer.Port}";

                ServerStatusText.Text = $"运行中 (:{_httpServer.Port})";
                ServerStatusText.Foreground = System.Windows.Media.Brushes.Green;
                StartServerBtn.Content = "停止服务器";
                ClientUrlHintText.Text = $"本机: {localUrl}    局域网: {_clientConnectUrl}";
                StatusInfoText.Text = $"服务器已启动 — 等待客户端连接（端口 {_httpServer.Port}）";
                ConfigureUpdateServer(_httpServer);
            }
            else
            {
                ServerStatusText.Text = "未启动";
                ServerStatusText.Foreground = System.Windows.Media.Brushes.Red;
                StartServerBtn.Content = "启动服务器";
                ClientUrlHintText.Text = "请先启动服务器";
                _clientConnectUrl = "";
            }
        }

        private void StartServerButton_Click(object sender, RoutedEventArgs e)
        {
            if (_httpServer != null && _httpServer.IsRunning)
            {
                _httpServer.Stop();
                _httpServer.ClientMessageReceived -= OnClientMessageReceived;
                _httpServer = null;
                ClientsDataGrid.ItemsSource = null;
                UpdateServerUi(false);
                StatusInfoText.Text = "服务器已停止";
                return;
            }

            StartServer();
        }

        private void CopyClientUrl_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_clientConnectUrl))
            {
                MessageBox.Show("服务器未运行，无法复制连接地址。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Clipboard.SetText($"{{\"server_url\": \"{_clientConnectUrl}\"}}");
            MessageBox.Show($"已复制到剪贴板：\n{{\"server_url\": \"{_clientConnectUrl}\"}}\n\n粘贴到客户端 control_config.json 即可。",
                "已复制", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnClientMessageReceived(string clientId, ClientMessage message)
        {
            Dispatcher.Invoke(() =>
            {
                var clientName = _httpServer?.Clients.TryGetValue(clientId, out var client) == true
                    ? client.Name
                    : clientId;
                ShowStatusMessage($"收到 {clientName} 的回复：{message.Text}");
                RefreshClientList();
            });
        }

        private void ShowStatusMessage(string message)
        {
            StatusInfoText.Text = message;
        }

        private static string EncodeClientId(string clientId)
        {
            return Uri.EscapeDataString(clientId ?? "");
        }

        private static string GetLocalIpAddress()
        {
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        return ip.ToString();
                }
            }
            catch { }
            return "127.0.0.1";
        }

        private async void RefreshClientList()
        {
            if (_httpServer == null || !_httpServer.IsRunning)
            {
                StatusInfoText.Text = "服务器未运行，请先点击「启动服务器」";
                ClientsDataGrid.ItemsSource = null;
                return;
            }

            try
            {
                var response = await _httpClient.GetAsync($"{_serverUrl}/api/clients");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var clients = JsonSerializer.Deserialize<List<ClientInfo>>(json, JsonOptions) ?? new List<ClientInfo>();

                    ClientsDataGrid.ItemsSource = clients;
                    if (clients.Count == 0)
                    {
                        StatusInfoText.Text = $"暂无客户端 — 请确认客户端 control_config.json 中 server_url 为: {_clientConnectUrl}";
                    }
                    else
                    {
                        StatusInfoText.Text = $"已加载 {clients.Count} 个客户端（端口 {_httpServer.Port}）";
                    }
                }
                else
                {
                    StatusInfoText.Text = $"刷新失败: HTTP {(int)response.StatusCode} — 请重启管理端";
                    ClientsDataGrid.ItemsSource = null;
                }
            }
            catch (Exception ex)
            {
                StatusInfoText.Text = $"刷新失败: {ex.Message}";
                ClientsDataGrid.ItemsSource = null;
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshClientList();
        }

        private async void LockClient_Click(object sender, RoutedEventArgs e)
        {
            var client = ClientsDataGrid.SelectedItem as ClientInfo;
            if (client == null)
            {
                MessageBox.Show("请选择一个客户端", "提示", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new DurationPromptWindow(
                "远程锁定",
                $"设置 {client.Name} 的锁定时长（分钟）：",
                30);
            dialog.Owner = this;
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                var body = JsonSerializer.Serialize(new { minutes = dialog.SelectedMinutes });
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(
                    $"{_serverUrl}/api/clients/{client.Id}/lock", content);
                
                if (response.IsSuccessStatusCode)
                {
                    ShowStatusMessage($"已向 {client.Name} 发送锁定命令（{dialog.SelectedMinutes} 分钟）");
                    RefreshClientList();
                }
                else
                {
                    MessageBox.Show($"发送失败，HTTP {(int)response.StatusCode}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"发送命令失败: {ex.Message}", "错误", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void UnlockClient_Click(object sender, RoutedEventArgs e)
        {
            var client = ClientsDataGrid.SelectedItem as ClientInfo;
            if (client == null)
            {
                MessageBox.Show("请选择一个客户端", "提示", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new DurationPromptWindow(
                "远程解锁",
                $"设置 {client.Name} 解锁后可使用时长（分钟）：",
                30);
            dialog.Owner = this;
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                var body = JsonSerializer.Serialize(new { minutes = dialog.SelectedMinutes });
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(
                    $"{_serverUrl}/api/clients/{client.Id}/unlock", content);
                
                if (response.IsSuccessStatusCode)
                {
                    ShowStatusMessage($"已向 {client.Name} 发送解锁命令（可使用 {dialog.SelectedMinutes} 分钟）");
                    RefreshClientList();
                }
                else
                {
                    MessageBox.Show($"发送失败，HTTP {(int)response.StatusCode}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"发送命令失败: {ex.Message}", "错误", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ShutdownClient_Click(object sender, RoutedEventArgs e)
        {
            var client = ClientsDataGrid.SelectedItem as ClientInfo;
            if (client == null)
            {
                MessageBox.Show("请选择一个客户端", "提示", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show(
                $"确定要远程关闭 {client.Name} 吗？", 
                "确认关机", 
                MessageBoxButton.YesNo, 
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    var response = await _httpClient.PostAsync(
                        $"{_serverUrl}/api/clients/{client.Id}/shutdown", null);
                    
                    if (response.IsSuccessStatusCode)
                    {
                        ShowStatusMessage($"已向 {client.Name} 发送关机命令");
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"发送命令失败: {ex.Message}", "错误", 
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void EditClientSettings_Click(object sender, RoutedEventArgs e)
        {
            var client = ClientsDataGrid.SelectedItem as ClientInfo;
            if (client == null)
            {
                MessageBox.Show("请选择一个客户端", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 从服务器获取最新数据（而非使用 DataGrid 的旧快照）
            ClientInfo freshClient;
            try
            {
                var response = await _httpClient.GetAsync($"{_serverUrl}/api/clients/{Uri.EscapeDataString(client.Id)}");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    freshClient = JsonSerializer.Deserialize<ClientInfo>(json, JsonOptions) ?? client;
                }
                else
                {
                    freshClient = client;
                }
            }
            catch
            {
                freshClient = client;
            }

            var settingsWindow = new ConfigEditorWindow(freshClient, _serverUrl);
            settingsWindow.Owner = this;
            if (settingsWindow.ShowDialog() == true)
                ShowStatusMessage($"已保存并下发 {freshClient.Name} 的设置{settingsWindow.StatusNote}");
            RefreshClientList();
        }

        private void EditConfig_Click(object sender, RoutedEventArgs e) => EditClientSettings_Click(sender, e);

        private void EditAppPolicy_Click(object sender, RoutedEventArgs e) => EditClientSettings_Click(sender, e);

        private async void PauseClient_Click(object sender, RoutedEventArgs e)
        {
            var client = ClientsDataGrid.SelectedItem as ClientInfo;
            if (client == null)
            {
                MessageBox.Show("请选择一个客户端", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show(
                $"确定要远程暂停 {client.Name} 的计时吗？\n客户端将锁屏并冻结剩余使用时间。",
                "确认暂停计时",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                var response = await _httpClient.PostAsync(
                    $"{_serverUrl}/api/clients/{client.Id}/pause", null);

                if (response.IsSuccessStatusCode)
                {
                    ShowStatusMessage($"已向 {client.Name} 发送暂停计时命令");
                    RefreshClientList();
                }
                else
                {
                    MessageBox.Show($"发送失败，HTTP {(int)response.StatusCode}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"发送命令失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void SendMessage_Click(object sender, RoutedEventArgs e)
        {
            var client = ClientsDataGrid.SelectedItem as ClientInfo;
            if (client == null)
            {
                MessageBox.Show("请选择一个客户端", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var msgWin = new Window
            {
                Title = $"消息 — {client.Name}",
                Width = 480,
                Height = 420,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                Content = new Grid { Margin = new Thickness(15) }
            };
            var grid = (Grid)msgWin.Content;
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var historyBox = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 10),
                FontSize = 13
            };
            Grid.SetRow(historyBox, 0);
            grid.Children.Add(historyBox);

            var textBox = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 60,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(textBox, 1);
            grid.Children.Add(textBox);

            var hint = new TextBlock
            {
                Text = "Ctrl+Enter 发送",
                Foreground = System.Windows.Media.Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(hint, 1);
            hint.VerticalAlignment = VerticalAlignment.Bottom;
            hint.HorizontalAlignment = HorizontalAlignment.Right;
            grid.Children.Add(hint);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var sendBtn = new Button { Content = "发送", Width = 80, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            var closeBtn = new Button { Content = "关闭", Width = 80, Height = 30 };
            btnPanel.Children.Add(sendBtn);
            btnPanel.Children.Add(closeBtn);
            Grid.SetRow(btnPanel, 2);
            grid.Children.Add(btnPanel);

            async Task LoadHistoryAsync()
            {
                try
                {
                    var response = await _httpClient.GetAsync(
                        $"{_serverUrl}/api/clients/{EncodeClientId(client.Id)}");
                    if (!response.IsSuccessStatusCode)
                        return;

                    var json = await response.Content.ReadAsStringAsync();
                    var fresh = JsonSerializer.Deserialize<ClientInfo>(json, JsonOptions);
                    var msgs = fresh?.ClientMessages ?? new List<ClientMessage>();
                    if (msgs.Count == 0)
                    {
                        historyBox.Text = "暂无消息记录";
                        return;
                    }

                    msgs.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
                    var lines = new List<string>();
                    foreach (var m in msgs)
                    {
                        var dir = m.Direction == "from_client" ? "客户端" : "管理端";
                        lines.Add($"[{m.Timestamp:HH:mm:ss}] {dir}：{m.Text}");
                    }
                    historyBox.Text = string.Join(Environment.NewLine, lines);
                    historyBox.ScrollToHome();
                }
                catch { }
            }

            async Task SendAsync()
            {
                var text = textBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(text))
                    return;

                try
                {
                    var body = JsonSerializer.Serialize(new { text });
                    var content = new StringContent(body, Encoding.UTF8, "application/json");
                    var response = await _httpClient.PostAsync(
                        $"{_serverUrl}/api/clients/{EncodeClientId(client.Id)}/message", content);

                    if (response.IsSuccessStatusCode)
                    {
                        textBox.Text = "";
                        ShowStatusMessage($"消息已发送给 {client.Name}");
                        await LoadHistoryAsync();
                    }
                    else
                    {
                        MessageBox.Show($"发送失败，HTTP {(int)response.StatusCode}", "错误",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"发送消息失败: {ex.Message}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }

            sendBtn.Click += async (s, args) => await SendAsync();
            closeBtn.Click += (s, args) => msgWin.Close();
            textBox.KeyDown += async (s, args) =>
            {
                if (args.Key == System.Windows.Input.Key.Enter &&
                    args.KeyboardDevice.Modifiers == System.Windows.Input.ModifierKeys.Control)
                {
                    await SendAsync();
                }
            };

            _messageRefreshTimer?.Stop();
            _messageRefreshTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _messageRefreshTimer.Tick += async (s, args) => await LoadHistoryAsync();
            _messageRefreshTimer.Start();
            msgWin.Closed += (s, args) => _messageRefreshTimer?.Stop();

            await LoadHistoryAsync();
            msgWin.ShowDialog();
        }

        private void ConfigureUpdateServer(HttpServer server)
        {
            if (server == null)
                return;

            server.PublicBaseUrl = _clientConnectUrl;
            server.ResolveUpdateVersion = () => _updateConfig.DefaultVersion;
            server.ResolveUpdatePackagePath = () => _updateConfig.GetLocalPackagePath();
        }

        private void LoadUpdateConfigToUi()
        {
            UpdatePackageUrlText.Text = _updateConfig.DefaultPackageUrl ?? "";
            UpdateVersionText.Text = _updateConfig.DefaultVersion ?? "";
            UpdateManifestUrlText.Text = _updateConfig.DefaultManifestUrl ?? "";
        }

        private void SaveUpdateConfig_Click(object sender, RoutedEventArgs e)
        {
            var previousVersion = _updateConfig.LastPushedVersion;

            _updateConfig.DefaultPackageUrl = UpdatePackageUrlText.Text.Trim();
            _updateConfig.DefaultVersion = UpdateVersionText.Text.Trim();
            _updateConfig.DefaultManifestUrl = UpdateManifestUrlText.Text.Trim();
            _updateConfig.Save();

            ConfigureUpdateServer(_httpServer);

            var packageUrl = _updateConfig.ResolvePackageUrl(_clientConnectUrl);
            var version = _updateConfig.DefaultVersion?.Trim();

            if (_updateConfig.AutoPushOnSave &&
                _httpServer != null &&
                !string.IsNullOrWhiteSpace(packageUrl) &&
                !string.IsNullOrWhiteSpace(version) &&
                !string.Equals(version, previousVersion, StringComparison.OrdinalIgnoreCase))
            {
                var count = _httpServer.PushUpdateToAllClients(packageUrl, version);
                _updateConfig.LastPushedVersion = version;
                _updateConfig.Save();
                StatusInfoText.Text = $"升级配置已保存，已向 {count} 个在线客户端推送 v{version}";
                return;
            }

            StatusInfoText.Text = "升级配置已保存";
        }

        private async void PushUpdate_Click(object sender, RoutedEventArgs e)
        {
            var client = ClientsDataGrid.SelectedItem as ClientInfo;
            if (client == null)
            {
                MessageBox.Show("请选择一个客户端", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SaveUpdateConfig_Click(sender, e);

            var packageUrl = _updateConfig.ResolvePackageUrl(_clientConnectUrl);
            if (string.IsNullOrWhiteSpace(packageUrl))
            {
                packageUrl = UpdatePackageUrlText.Text.Trim();
            }

            var version = UpdateVersionText.Text.Trim();

            if (string.IsNullOrWhiteSpace(packageUrl))
            {
                MessageBox.Show("请先填写升级包地址，或将 ZIP 放入 updates 文件夹", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show(
                $"确定向 {client.Name} 推送升级吗？\n\n版本: {(string.IsNullOrWhiteSpace(version) ? "未指定" : version)}\n地址: {packageUrl}\n\n客户端将在锁屏时自动安装。",
                "确认升级",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
                return;

            try
            {
                var payload = new Dictionary<string, string>
                {
                    { "package_url", packageUrl }
                };
                if (!string.IsNullOrWhiteSpace(version))
                    payload["version"] = version;

                var body = JsonSerializer.Serialize(payload);
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(
                    $"{_serverUrl}/api/clients/{client.Id}/update", content);

                if (response.IsSuccessStatusCode)
                {
                    ShowStatusMessage($"已向 {client.Name} 发送升级命令（锁屏时自动安装）");
                }
                else
                {
                    MessageBox.Show($"推送失败，HTTP {(int)response.StatusCode}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"推送升级失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _httpServer?.Stop();
            _refreshTimer?.Stop();
            base.OnClosed(e);
        }
    }
}
