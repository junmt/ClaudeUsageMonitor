using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ClaudeUsageMonitor.Core.Cli;
using ClaudeUsageMonitor.Core.Settings;
using Microsoft.Win32;

namespace ClaudeUsageMonitor.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _original;

    public SettingsWindow(AppSettings settings, ClaudeEndpoint? lastEndpoint)
    {
        InitializeComponent();
        _original = settings;
        Load(settings, lastEndpoint);
    }

    /// <summary>保存が押された場合の新しい設定。</summary>
    public AppSettings? Result { get; private set; }

    private void Load(AppSettings s, ClaudeEndpoint? lastEndpoint)
    {
        // インストーラーで自動起動を設定した場合もあるため、実際の登録状態を表示する
        AutoStartCheck.IsChecked = s.AutoStart || ClaudeUsageMonitor.Services.StartupRegistration.IsEnabled();
        NotificationsCheck.IsChecked = s.NotificationsEnabled;
        NotifyResetCheck.IsChecked = s.NotifyOnReset;

        SelectByTag(SourceCombo, s.Source.ToString());
        IntervalBox.Text = s.RefreshIntervalMinutes.ToString(CultureInfo.InvariantCulture);
        ClaudePathBox.Text = s.ClaudePath ?? "";

        var windowsClaude = ClaudeCliService.FindWindowsClaude(null);
        DetectionText.Text =
            $"Windows: {windowsClaude ?? "見つかりません（WSL 側から取得します）"}\n"
            + $"最後に取得できた取得元: {lastEndpoint?.ToString() ?? "なし"}";

        var distros = Wsl.ListDistros();
        if (distros.Count == 0)
        {
            WslPanel.Children.Add(new TextBlock { Text = "（WSL ディストリビューションがありません）", Foreground = System.Windows.Media.Brushes.Gray });
        }
        foreach (var distro in distros)
        {
            WslPanel.Children.Add(new CheckBox
            {
                Content = distro.IsDefault ? $"{distro.Name}（既定）" : distro.Name,
                Tag = distro.Name,
                IsChecked = s.IsWslDistroEnabled(distro.Name),
            });
        }
        StartStoppedWslCheck.IsChecked = s.StartStoppedWslForLogs;
        ExtraDirsBox.Text = string.Join(Environment.NewLine, s.ExtraLogDirectories);

        WarningBox.Text = s.WarningThreshold.ToString(CultureInfo.InvariantCulture);
        CriticalBox.Text = s.CriticalThreshold.ToString(CultureInfo.InvariantCulture);
        SelectByTag(ThemeCombo, s.Theme.ToString());
        SelectByTag(TopmostCombo, s.Topmost.ToString());
        OpacitySlider.Value = s.OpacityPercent;
        ShowEmailCheck.IsChecked = s.ShowEmail;
        ShowPcCpuCheck.IsChecked = s.ShowPcCpu;
        ShowPcMemoryCheck.IsChecked = s.ShowPcMemory;
        ShowPcStorageCheck.IsChecked = s.ShowPcStorage;
        StorageDrivesBox.Text = s.StorageDrives ?? "";
        ShowWslCpuCheck.IsChecked = s.ShowWslCpu;
        ShowWslMemoryCheck.IsChecked = s.ShowWslMemory;
        ShowWslStorageCheck.IsChecked = s.ShowWslStorage;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!TryParseInt(IntervalBox, 1, 30, "取得間隔", out var interval)
            || !TryParseInt(WarningBox, 1, 100, "警告しきい値", out var warning)
            || !TryParseInt(CriticalBox, 1, 100, "危険しきい値", out var critical))
            return;
        if (critical < warning)
        {
            MessageBox.Show(this, "危険しきい値は警告しきい値以上にしてください。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var claudePath = ClaudePathBox.Text.Trim();
        if (claudePath.Length > 0 && !System.IO.File.Exists(claudePath))
        {
            MessageBox.Show(this, "指定された Claude Code のパスが見つかりません。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = new AppSettings
        {
            AutoStart = AutoStartCheck.IsChecked == true,
            NotificationsEnabled = NotificationsCheck.IsChecked == true,
            NotifyOnReset = NotifyResetCheck.IsChecked == true,
            Source = Enum.Parse<SourceMode>(SelectedTag(SourceCombo)),
            RefreshIntervalMinutes = interval,
            ClaudePath = claudePath.Length > 0 ? claudePath : null,
            WslDistros = WslPanel.Children.OfType<CheckBox>().ToDictionary(c => (string)c.Tag, c => c.IsChecked == true),
            StartStoppedWslForLogs = StartStoppedWslCheck.IsChecked == true,
            ExtraLogDirectories = ExtraDirsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            WarningThreshold = warning,
            CriticalThreshold = critical,
            Theme = Enum.Parse<ColorTheme>(SelectedTag(ThemeCombo)),
            Topmost = Enum.Parse<TopmostMode>(SelectedTag(TopmostCombo)),
            OpacityPercent = (int)OpacitySlider.Value,
            ShowEmail = ShowEmailCheck.IsChecked == true,
            ShowPcCpu = ShowPcCpuCheck.IsChecked == true,
            ShowPcMemory = ShowPcMemoryCheck.IsChecked == true,
            ShowPcStorage = ShowPcStorageCheck.IsChecked == true,
            StorageDrives = string.IsNullOrWhiteSpace(StorageDrivesBox.Text) ? null : StorageDrivesBox.Text.Trim(),
            ShowWslCpu = ShowWslCpuCheck.IsChecked == true,
            ShowWslMemory = ShowWslMemoryCheck.IsChecked == true,
            ShowWslStorage = ShowWslStorageCheck.IsChecked == true,

            // ウィジェットの状態は設定画面では変更しない
            ClickThrough = _original.ClickThrough,
            WindowLeft = _original.WindowLeft,
            WindowTop = _original.WindowTop,
            DetailMode = _original.DetailMode,
            WidgetVisible = _original.WidgetVisible,
        };
        DialogResult = true;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Claude Code の実行ファイルを選択",
            Filter = "Claude Code (claude.exe;claude.cmd)|claude.exe;claude.cmd|すべてのファイル (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            ClaudePathBox.Text = dialog.FileName;
    }

    private bool TryParseInt(TextBox box, int min, int max, string name, out int value)
    {
        if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min && value <= max)
            return true;
        MessageBox.Show(this, $"{name}は {min}〜{max} の整数で入力してください。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        box.Focus();
        return false;
    }

    private static void SelectByTag(ComboBox combo, string tag) =>
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? combo.Items[0];

    private static string SelectedTag(ComboBox combo) => (string)((ComboBoxItem)combo.SelectedItem).Tag;
}
