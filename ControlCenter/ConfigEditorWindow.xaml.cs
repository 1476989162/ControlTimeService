using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ControlTimeService;

namespace ControlCenter
{
    public partial class ConfigEditorWindow : Window
    {
        private static readonly JsonSerializerOptions ConfigJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private ClientInfo _client;
        private string _serverUrl;
        private Dictionary<string, DaySchedule> _config;
        private HttpClient _httpClient;

        public string StatusNote { get; private set; } = "";

        public ConfigEditorWindow(ClientInfo client, string serverUrl)
        {
            InitializeComponent();
            _client = client;
            _serverUrl = serverUrl.TrimEnd('/');
            _httpClient = new HttpClient();
            Title = $"客户端设置 — {client.Name ?? client.Id}";

            _config = client.Config ?? CreateDefaultConfig();

            InitializeTabs();
        }

        private Dictionary<string, DaySchedule> CreateDefaultConfig()
        {
            var config = new Dictionary<string, DaySchedule>();
            foreach (DayOfWeek day in Enum.GetValues(typeof(DayOfWeek)))
            {
                config[day.ToString()] = new DaySchedule
                {
                    Day = day,
                    UsageMinutes = 30,
                    RestMinutes = 30,
                    Enabled = true,
                    LunchRestrictionEnabled = true,
                    LunchMaxUsageMinutes = 60,
                    LunchStartTime = "11:00",
                    LunchEndTime = "14:00",
                    EveningRestrictionEnabled = true,
                    EveningMaxUsageMinutes = 30,
                    EveningStartTime = "18:00",
                    EveningEndTime = "20:30",
                    NightShutdownTime = "20:30"
                };
            }
            return config;
        }

        private void InitializeTabs()
        {
            string[] dayNames = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
            DayOfWeek[] days = { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, 
                                DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };

            for (int i = 0; i < 7; i++)
            {
                var tabItem = new TabItem { Header = dayNames[i] };
                var content = CreateDayConfigPanel(days[i]);
                tabItem.Content = content;
                DayTabs.Items.Add(tabItem);
            }
        }

        private ScrollViewer CreateDayConfigPanel(DayOfWeek day)
        {
            var schedule = _config[day.ToString()];
            var scrollViewer = new ScrollViewer { Padding = new Thickness(10) };
            var stackPanel = new StackPanel { Margin = new Thickness(10) };

            // 启用/禁用
            var enabledCheckBox = new CheckBox 
            { 
                Content = "启用此天的时间控制", 
                IsChecked = schedule.Enabled,
                Margin = new Thickness(0, 0, 0, 15),
                Tag = "Enabled"
            };
            stackPanel.Children.Add(enabledCheckBox);

            // 使用时长
            stackPanel.Children.Add(CreateTimeInput("使用时长（分钟）:", schedule.UsageMinutes.ToString(), "UsageMinutes"));

            // 休息时长
            stackPanel.Children.Add(CreateTimeInput("休息时长（分钟）:", schedule.RestMinutes.ToString(), "RestMinutes"));

            // 午间规则
            var lunchGroup = new GroupBox { Header = "午间规则", Margin = new Thickness(0, 15, 0, 10) };
            var lunchPanel = new StackPanel { Margin = new Thickness(10) };
            
            var lunchEnabledCheckBox = new CheckBox 
            { 
                Content = "启用午间限制", 
                IsChecked = schedule.LunchRestrictionEnabled,
                Margin = new Thickness(0, 0, 0, 10),
                Tag = "LunchEnabled"
            };
            lunchPanel.Children.Add(lunchEnabledCheckBox);
            lunchPanel.Children.Add(CreateTimeInput("最大使用时长（分钟）:", schedule.LunchMaxUsageMinutes.ToString(), "LunchMaxUsage"));
            lunchPanel.Children.Add(CreateTimeRangeInput("时间段:", schedule.LunchStartTime, schedule.LunchEndTime, "LunchTime"));
            
            lunchGroup.Content = lunchPanel;
            stackPanel.Children.Add(lunchGroup);

            // 晚间规则
            var eveningGroup = new GroupBox { Header = "晚间规则", Margin = new Thickness(0, 10, 0, 10) };
            var eveningPanel = new StackPanel { Margin = new Thickness(10) };
            
            var eveningEnabledCheckBox = new CheckBox 
            { 
                Content = "启用晚间限制", 
                IsChecked = schedule.EveningRestrictionEnabled,
                Margin = new Thickness(0, 0, 0, 10),
                Tag = "EveningEnabled"
            };
            eveningPanel.Children.Add(eveningEnabledCheckBox);
            eveningPanel.Children.Add(CreateTimeInput("最大使用时长（分钟）:", schedule.EveningMaxUsageMinutes.ToString(), "EveningMaxUsage"));
            eveningPanel.Children.Add(CreateTimeRangeInput("时间段:", schedule.EveningStartTime, schedule.EveningEndTime, "EveningTime"));
            
            eveningGroup.Content = eveningPanel;
            stackPanel.Children.Add(eveningGroup);

            // 夜间关机时间
            stackPanel.Children.Add(CreateTimeInput("夜间关机时间 (HH:mm):", schedule.NightShutdownTime, "NightShutdown"));

            // 早晨锁定
            var morningGroup = new GroupBox { Header = "早晨锁定", Margin = new Thickness(0, 10, 0, 10) };
            var morningPanel = new StackPanel { Margin = new Thickness(10) };

            var morningEnabledCheckBox = new CheckBox
            {
                Content = "启用早晨锁定（解锁时间前强制锁屏）",
                IsChecked = schedule.MorningLockEnabled,
                Margin = new Thickness(0, 0, 0, 10),
                Tag = "MorningLockEnabled"
            };
            morningPanel.Children.Add(morningEnabledCheckBox);
            morningPanel.Children.Add(CreateTimeInput("解锁时间 (HH:mm):", schedule.MorningUnlockTime, "MorningUnlock"));

            morningGroup.Content = morningPanel;
            stackPanel.Children.Add(morningGroup);

            // 应用权限（每天独立配置）
            var appPolicyGroup = new GroupBox { Header = "应用权限", Margin = new Thickness(0, 10, 0, 10) };
            var appPolicyPanel = new StackPanel { Margin = new Thickness(10) };

            appPolicyPanel.Children.Add(new TextBlock
            {
                Text = "勾选表示允许使用，未勾选则检测到后会锁屏",
                Margin = new Thickness(0, 0, 0, 10),
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap
            });

            appPolicyPanel.Children.Add(CreatePolicyCheckBox("允许观看视频（B站、爱奇艺、优酷等）", schedule.AllowVideo, "AllowVideo"));
            appPolicyPanel.Children.Add(CreatePolicyCheckBox("允许微信小游戏", schedule.AllowWeChatMiniGames, "AllowWeChatMiniGames"));
            appPolicyPanel.Children.Add(CreatePolicyCheckBox("允许猫箱", schedule.AllowMaoxiang, "AllowMaoxiang"));
            appPolicyPanel.Children.Add(CreatePolicyCheckBox("允许抖音", schedule.AllowDouyin, "AllowDouyin"));
            appPolicyPanel.Children.Add(CreatePolicyCheckBox("允许番茄小说", schedule.AllowFanqieNovel, "AllowFanqieNovel"));
            appPolicyPanel.Children.Add(CreatePolicyCheckBox("允许腾讯应用宝（不含其内游戏）", schedule.AllowTencentAppStore, "AllowTencentAppStore"));
            appPolicyPanel.Children.Add(CreatePolicyCheckBox("允许其他游戏（Steam 等）", schedule.AllowOtherGames, "AllowOtherGames"));
            appPolicyPanel.Children.Add(CreatePolicyCheckBox("拦截抖音/豆包游戏视频（允许抖音时仍生效）", schedule.BlockDouyinGameVideos, "BlockDouyinGameVideos"));
            appPolicyPanel.Children.Add(CreatePolicyCheckBox("监控豆包内打开的抖音", schedule.MonitorDoubao, "MonitorDoubao"));
            appPolicyPanel.Children.Add(CreateTimeInput("游戏视频关闭阈值（秒）:", schedule.DouyinGameVideoThresholdSeconds.ToString(), "DouyinGameThreshold"));

            appPolicyGroup.Content = appPolicyPanel;
            stackPanel.Children.Add(appPolicyGroup);

            // 存储 day 信息到 Tag
            stackPanel.Tag = day;

            scrollViewer.Content = stackPanel;
            return scrollViewer;
        }

        private CheckBox CreatePolicyCheckBox(string content, bool isChecked, string tag)
        {
            return new CheckBox
            {
                Content = content,
                IsChecked = isChecked,
                Margin = new Thickness(0, 0, 0, 10),
                Tag = tag
            };
        }

        private StackPanel CreateTimeInput(string label, string value, string tag)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 5, 0, 5) };
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 5) });
            
            var textBox = new TextBox 
            { 
                Text = value, 
                Width = 100, 
                HorizontalAlignment = HorizontalAlignment.Left,
                Tag = tag
            };
            panel.Children.Add(textBox);
            
            return panel;
        }

        private StackPanel CreateTimeRangeInput(string label, string startTime, string endTime, string tag)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 5, 0, 5) };
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 5) });
            
            var timePanel = new StackPanel { Orientation = Orientation.Horizontal };
            
            var startBox = new TextBox { Text = startTime, Width = 60, Tag = tag + "_Start" };
            timePanel.Children.Add(startBox);
            timePanel.Children.Add(new TextBlock { Text = " 至 ", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 5, 0) });
            var endBox = new TextBox { Text = endTime, Width = 60, Tag = tag + "_End" };
            timePanel.Children.Add(endBox);
            
            panel.Children.Add(timePanel);
            return panel;
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 收集所有配置
                foreach (TabItem tabItem in DayTabs.Items)
                {
                    var scrollViewer = tabItem.Content as ScrollViewer;
                    var stackPanel = scrollViewer?.Content as StackPanel;
                    
                    if (stackPanel == null || !(stackPanel.Tag is DayOfWeek)) continue;
                    
                    var day = (DayOfWeek)stackPanel.Tag;
                    var schedule = _config[day.ToString()];

                    // 更新配置
                    foreach (var child in GetAllControls(stackPanel))
                    {
                        if (child is CheckBox checkBox)
                        {
                            switch (checkBox.Tag?.ToString())
                            {
                                case "Enabled":
                                    schedule.Enabled = checkBox.IsChecked ?? false;
                                    break;
                                case "LunchEnabled":
                                    schedule.LunchRestrictionEnabled = checkBox.IsChecked ?? false;
                                    break;
                                case "EveningEnabled":
                                    schedule.EveningRestrictionEnabled = checkBox.IsChecked ?? false;
                                    break;
                                case "MorningLockEnabled":
                                    schedule.MorningLockEnabled = checkBox.IsChecked ?? false;
                                    break;
                                // 应用权限
                                case "AllowVideo":
                                    schedule.AllowVideo = checkBox.IsChecked ?? false;
                                    break;
                                case "AllowWeChatMiniGames":
                                    schedule.AllowWeChatMiniGames = checkBox.IsChecked ?? false;
                                    break;
                                case "AllowMaoxiang":
                                    schedule.AllowMaoxiang = checkBox.IsChecked ?? false;
                                    break;
                                case "AllowDouyin":
                                    schedule.AllowDouyin = checkBox.IsChecked ?? false;
                                    break;
                                case "AllowFanqieNovel":
                                    schedule.AllowFanqieNovel = checkBox.IsChecked ?? false;
                                    break;
                                case "AllowTencentAppStore":
                                    schedule.AllowTencentAppStore = checkBox.IsChecked ?? false;
                                    break;
                                case "AllowOtherGames":
                                    schedule.AllowOtherGames = checkBox.IsChecked ?? false;
                                    break;
                                case "BlockDouyinGameVideos":
                                    schedule.BlockDouyinGameVideos = checkBox.IsChecked ?? false;
                                    break;
                                case "MonitorDoubao":
                                    schedule.MonitorDoubao = checkBox.IsChecked ?? false;
                                    break;
                            }
                        }
                        else if (child is TextBox textBox)
                        {
                            switch (textBox.Tag?.ToString())
                            {
                                case "UsageMinutes":
                                    if (int.TryParse(textBox.Text, out int usage))
                                        schedule.UsageMinutes = usage;
                                    break;
                                case "RestMinutes":
                                    if (int.TryParse(textBox.Text, out int rest))
                                        schedule.RestMinutes = rest;
                                    break;
                                case "LunchMaxUsage":
                                    if (int.TryParse(textBox.Text, out int lunchMax))
                                        schedule.LunchMaxUsageMinutes = lunchMax;
                                    break;
                                case "LunchTime_Start":
                                    schedule.LunchStartTime = textBox.Text;
                                    break;
                                case "LunchTime_End":
                                    schedule.LunchEndTime = textBox.Text;
                                    break;
                                case "EveningMaxUsage":
                                    if (int.TryParse(textBox.Text, out int eveningMax))
                                        schedule.EveningMaxUsageMinutes = eveningMax;
                                    break;
                                case "EveningTime_Start":
                                    schedule.EveningStartTime = textBox.Text;
                                    break;
                                case "EveningTime_End":
                                    schedule.EveningEndTime = textBox.Text;
                                    break;
                                case "NightShutdown":
                                    schedule.NightShutdownTime = textBox.Text;
                                    break;
                                case "MorningUnlock":
                                    schedule.MorningUnlockTime = textBox.Text;
                                    break;
                                case "DouyinGameThreshold":
                                    if (int.TryParse(textBox.Text, out int threshold) && threshold >= 1)
                                        schedule.DouyinGameVideoThresholdSeconds = threshold;
                                    break;
                            }
                        }
                    }
                }

                // 配置中已包含应用权限（每天独立），只需发送 config
                var configJson = JsonSerializer.Serialize(_config, ConfigJsonOptions);
                var configContent = new StringContent(configJson, Encoding.UTF8, "application/json");
                var configResponse = await _httpClient.PostAsync(
                    $"{_serverUrl}/api/clients/{Uri.EscapeDataString(_client.Id)}/config", configContent);

                if (configResponse.IsSuccessStatusCode)
                {
                    StatusNote = _client.Status == "Offline"
                        ? "（客户端离线，上线后自动下发）"
                        : "";
                    DialogResult = true;
                    Close();
                }
                else
                {
                    MessageBox.Show(
                        $"保存失败：时间配置 HTTP {(int)configResponse.StatusCode}",
                        "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败：{ex.Message}", "错误", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        private System.Collections.Generic.IEnumerable<System.Windows.Controls.Control> GetAllControls(DependencyObject parent)
        {
            var controls = new System.Collections.Generic.List<System.Windows.Controls.Control>();

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                if (child is System.Windows.Controls.Control control)
                    controls.Add(control);

                controls.AddRange(GetAllControls(child));
            }

            return controls;
        }
    }
}
