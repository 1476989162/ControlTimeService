using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ControlTimeService;

namespace ControlCenter
{
    public partial class AppPolicyEditorWindow : Window
    {
        private ClientInfo _client;
        private string _serverUrl;
        private AppPolicy _policy;
        private HttpClient _httpClient;

        private CheckBox _allowVideoCheck;
        private CheckBox _allowWeChatMiniGamesCheck;
        private CheckBox _allowMaoxiangCheck;
        private CheckBox _allowDouyinCheck;
        private CheckBox _allowFanqieNovelCheck;
        private CheckBox _allowTencentAppStoreCheck;
        private CheckBox _allowOtherGamesCheck;

        public AppPolicyEditorWindow(ClientInfo client, string serverUrl)
        {
            InitializeComponent();
            _client = client;
            _serverUrl = serverUrl.TrimEnd('/');
            _httpClient = new HttpClient();
            _policy = client.AppPolicy?.Clone() ?? AppPolicy.CreateDefault();

            BuildUi();
        }

        private void BuildUi()
        {
            Title = $"应用权限 - {_client.Name}";
            Width = 450;
            Height = 380;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new Grid { Margin = new Thickness(15) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var stackPanel = new StackPanel();
            stackPanel.Children.Add(new TextBlock
            {
                Text = "勾选表示允许使用，未勾选则检测到后会锁屏",
                Margin = new Thickness(0, 0, 0, 15),
                Foreground = System.Windows.Media.Brushes.Gray,
                TextWrapping = TextWrapping.Wrap
            });

            _allowVideoCheck = CreateCheckBox("允许观看视频（B站、爱奇艺、优酷等）", _policy.AllowVideo);
            _allowWeChatMiniGamesCheck = CreateCheckBox("允许微信小游戏", _policy.AllowWeChatMiniGames);
            _allowMaoxiangCheck = CreateCheckBox("允许猫箱", _policy.AllowMaoxiang);
            _allowDouyinCheck = CreateCheckBox("允许抖音", _policy.AllowDouyin);
            _allowFanqieNovelCheck = CreateCheckBox("允许番茄小说", _policy.AllowFanqieNovel);
            _allowTencentAppStoreCheck = CreateCheckBox("允许腾讯应用宝（不含其内游戏）", _policy.AllowTencentAppStore);
            _allowOtherGamesCheck = CreateCheckBox("允许其他游戏（Steam 等）", _policy.AllowOtherGames);

            stackPanel.Children.Add(_allowVideoCheck);
            stackPanel.Children.Add(_allowWeChatMiniGamesCheck);
            stackPanel.Children.Add(_allowMaoxiangCheck);
            stackPanel.Children.Add(_allowDouyinCheck);
            stackPanel.Children.Add(_allowFanqieNovelCheck);
            stackPanel.Children.Add(_allowTencentAppStoreCheck);
            stackPanel.Children.Add(_allowOtherGamesCheck);

            root.Children.Add(stackPanel);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 15, 0, 0)
            };
            Grid.SetRow(buttonPanel, 1);

            var saveBtn = new Button { Content = "保存并应用", Width = 100, Height = 30, Margin = new Thickness(5) };
            saveBtn.Click += SaveButton_Click;
            var cancelBtn = new Button { Content = "取消", Width = 80, Height = 30, Margin = new Thickness(5) };
            cancelBtn.Click += (s, e) => { DialogResult = false; Close(); };

            buttonPanel.Children.Add(saveBtn);
            buttonPanel.Children.Add(cancelBtn);
            root.Children.Add(buttonPanel);

            Content = root;
        }

        private CheckBox CreateCheckBox(string content, bool isChecked)
        {
            return new CheckBox
            {
                Content = content,
                IsChecked = isChecked,
                Margin = new Thickness(0, 0, 0, 10)
            };
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var policy = new AppPolicy
                {
                    AllowVideo = _allowVideoCheck.IsChecked ?? false,
                    AllowWeChatMiniGames = _allowWeChatMiniGamesCheck.IsChecked ?? false,
                    AllowMaoxiang = _allowMaoxiangCheck.IsChecked ?? false,
                    AllowDouyin = _allowDouyinCheck.IsChecked ?? false,
                    AllowFanqieNovel = _allowFanqieNovelCheck.IsChecked ?? false,
                    AllowTencentAppStore = _allowTencentAppStoreCheck.IsChecked ?? false,
                    AllowOtherGames = _allowOtherGamesCheck.IsChecked ?? false
                };

                var json = JsonSerializer.Serialize(policy);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(
                    $"{_serverUrl}/api/clients/{_client.Id}/app-policy", content);

                if (response.IsSuccessStatusCode)
                {
                    MessageBox.Show("应用权限已保存并发送到客户端！", "提示",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    DialogResult = true;
                    Close();
                }
                else
                {
                    MessageBox.Show("保存失败，请检查网络连接", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
