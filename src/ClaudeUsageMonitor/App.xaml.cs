using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using ClaudeUsageMonitor.Core.Infrastructure;
using ClaudeUsageMonitor.Core.Settings;
using ClaudeUsageMonitor.Services;
using ClaudeUsageMonitor.ViewModels;
using ClaudeUsageMonitor.Views;
using Microsoft.Win32;

namespace ClaudeUsageMonitor;

public partial class App : Application
{
    private const string MutexName = @"Local\ClaudeUsageMonitor.SingleInstance";

    private Mutex? _singleInstance;
    private bool _ownsMutex;
    private MonitorController? _controller;
    private TrayIconManager? _tray;
    private NotificationManager? _notifications;
    private WidgetWindow? _widget;
    private SettingsWindow? _settingsWindow;
    private readonly WidgetViewModel _viewModel = new();
    private DispatcherTimer? _clockTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, MutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("Claude Usage Monitor は既に起動しています。タスクトレイを確認してください。",
                "Claude Usage Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("未処理の例外が発生しました", args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("未処理のタスク例外が発生しました", args.Exception);
            args.SetObserved();
        };

        AppPaths.EnsureCreated();
        AppLog.Info("起動しました");

        var settings = JsonStore.Load<AppSettings>(AppPaths.SettingsFile) ?? new AppSettings();
        settings.Normalize();
        ThemeManager.Apply(settings.Theme);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _controller = new MonitorController(settings);
        _tray = new TrayIconManager();
        _notifications = new NotificationManager((title, text, warning) =>
            Dispatcher.BeginInvoke(() => _tray?.ShowNotification(title, text, warning)));

        _tray.ToggleWidgetRequested += ToggleWidget;
        _tray.ToggleDetailRequested += ToggleDetail;
        _tray.ToggleClickThroughRequested += ToggleClickThrough;
        _tray.RefreshRequested += () => _controller.RefreshAll();
        _tray.SettingsRequested += ShowSettings;
        _tray.OpenLogsRequested += () => Process.Start(new ProcessStartInfo(AppPaths.LogsDir) { UseShellExecute = true });
        _tray.ExitRequested += () => Shutdown();

        _widget = new WidgetWindow(_viewModel);
        _widget.RefreshRequested += () => _controller.RefreshAll();
        _widget.SettingsRequested += ShowSettings;
        _widget.HideRequested += () => SetWidgetVisible(false);
        _widget.DetailToggled += ToggleDetail;
        _widget.PositionChanged += SaveWidgetPosition;
        _widget.ApplySettings(settings);
        _widget.RestorePosition(settings.WindowLeft, settings.WindowTop);
        if (settings.WidgetVisible)
            _widget.Show();
        _widget.ApplySettings(settings); // ハンドル作成後に必要な設定（クリック透過など）を反映

        _controller.Changed += () => Dispatcher.BeginInvoke(UpdateUi);
        _controller.Start();

        // 「Resets in 2h」などの相対表示を更新する
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clockTimer.Tick += (_, _) => UpdateUi();
        _clockTimer.Start();

        UpdateUi();
    }

    private void UpdateUi()
    {
        if (_controller is null || _tray is null)
            return;

        var now = DateTimeOffset.Now;
        _viewModel.Update(_controller, now);
        _tray.Update(_controller.Snapshot, _controller.Settings, now, _widget?.IsVisible == true, _controller.Error);
        _notifications?.Process(_controller.Snapshot, _controller.Settings);
    }

    private void ToggleWidget() => SetWidgetVisible(_widget?.IsVisible != true);

    private void SetWidgetVisible(bool visible)
    {
        if (_widget is null || _controller is null)
            return;

        if (visible)
        {
            _widget.Show();
            _widget.ApplySettings(_controller.Settings);
        }
        else
        {
            _widget.Hide();
        }
        _controller.Settings.WidgetVisible = visible;
        _controller.SaveSettings();
        UpdateUi();
    }

    private void ToggleDetail()
    {
        if (_widget is null || _controller is null)
            return;
        _controller.Settings.DetailMode = !_controller.Settings.DetailMode;
        _widget.SetDetail(_controller.Settings.DetailMode);
        _controller.SaveSettings();
        UpdateUi();
    }

    private void ToggleClickThrough()
    {
        if (_widget is null || _controller is null)
            return;
        _controller.Settings.ClickThrough = !_controller.Settings.ClickThrough;
        _widget.SetClickThrough(_controller.Settings.ClickThrough);
        _controller.SaveSettings();
        UpdateUi();
    }

    private void SaveWidgetPosition()
    {
        if (_widget is null || _controller is null)
            return;
        _controller.Settings.WindowLeft = _widget.Left;
        _controller.Settings.WindowTop = _widget.Top;
        _controller.SaveSettings();
    }

    private void ShowSettings()
    {
        if (_controller is null)
            return;
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_controller.Settings, _controller.LastEndpoint);
        try
        {
            if (_settingsWindow.ShowDialog() == true && _settingsWindow.Result is { } updated)
            {
                StartupRegistration.Apply(updated.AutoStart);
                _controller.ApplySettings(updated);
                ThemeManager.Apply(updated.Theme);
                _widget?.ApplySettings(updated);
                UpdateUi();
            }
        }
        finally
        {
            _settingsWindow = null;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && _controller?.Settings.Theme == ColorTheme.System)
        {
            Dispatcher.BeginInvoke(() =>
            {
                ThemeManager.Apply(ColorTheme.System);
                UpdateUi();
            });
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _clockTimer?.Stop();
        if (_widget is not null && _controller is not null && _widget.IsVisible)
            SaveWidgetPosition();
        _controller?.Dispose();
        _tray?.Dispose();
        if (_ownsMutex)
        {
            AppLog.Info("終了しました");
            _singleInstance?.ReleaseMutex();
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
