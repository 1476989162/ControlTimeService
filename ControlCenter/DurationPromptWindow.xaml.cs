using System.Windows;

namespace ControlCenter
{
    public partial class DurationPromptWindow : Window
    {
        public int SelectedMinutes { get; private set; } = 30;

        public DurationPromptWindow(string title, string prompt, int defaultMinutes = 30)
        {
            InitializeComponent();
            Title = title;
            PromptText.Text = prompt;
            MinutesCombo.ItemsSource = new[] { 5, 10, 15, 20, 30, 45, 60, 90, 120 };
            MinutesCombo.SelectedItem = defaultMinutes;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (MinutesCombo.SelectedItem is int minutes)
            {
                SelectedMinutes = minutes;
                DialogResult = true;
                Close();
            }
            else
            {
                MessageBox.Show("请选择时长。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
