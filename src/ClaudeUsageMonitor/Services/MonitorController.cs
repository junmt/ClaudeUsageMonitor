using System.Net.NetworkInformation;
using ClaudeUsageMonitor.Core.Cli;
using ClaudeUsageMonitor.Core.Infrastructure;
using ClaudeUsageMonitor.Core.Logs;
using ClaudeUsageMonitor.Core.Models;
using ClaudeUsageMonitor.Core.Settings;
using Microsoft.Win32;

namespace ClaudeUsageMonitor.Services;

/// <summary>
/// 利用枠の取得（Claude Code CLI）とローカルログの集計を定期的に実行し、状態を保持する。
/// Changed はバックグラウンドスレッドから発火するため、UI 側でディスパッチすること。
/// </summary>
public sealed class MonitorController : IDisposable
{
    private static readonly TimeSpan LogPollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AccountInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinFetchIntervalOnActivity = TimeSpan.FromSeconds(60);

    private readonly Lock _settingsGate = new();
    private readonly ClaudeCliService _cli;
    private readonly UsageRepository _repository;
    private readonly LogCollector _collector;
    private readonly Timer _usageTimer;
    private readonly Timer _logTimer;
    private readonly Timer _accountTimer;
    private readonly CancellationTokenSource _shutdown = new();
    private int _fetching;
    private DateTimeOffset _lastFetchStarted = DateTimeOffset.MinValue;

    public MonitorController(AppSettings settings)
    {
        Settings = settings;
        _cli = new ClaudeCliService(() => Settings);
        _repository = new UsageRepository(AppPaths.DatabaseFile);
        _collector = new LogCollector(_repository, ResolveLogSourcesAsync);
        _collector.RecordsAdded += OnRecordsAdded;

        _usageTimer = new Timer(_ => _ = RefreshUsageAsync());
        _logTimer = new Timer(_ => _ = ScanLogsAsync());
        _accountTimer = new Timer(_ => _ = RefreshAccountAsync());

        Snapshot = JsonStore.Load<RateLimitSnapshot>(AppPaths.RateLimitCacheFile);
        Account = Snapshot?.Account;
    }

    public event Action? Changed;

    public AppSettings Settings { get; private set; }
    public RateLimitSnapshot? Snapshot { get; private set; }
    public AccountInfo? Account { get; private set; }
    public UsageStats? Stats { get; private set; }

    /// <summary>直近の取得エラー（成功したら null）。</summary>
    public string? Error { get; private set; }

    public bool ClaudeNotFound { get; private set; }
    public ClaudeEndpoint? LastEndpoint => _cli.LastEndpoint;
    public bool IsFetching => Volatile.Read(ref _fetching) == 1;

    public void Start()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

        _usageTimer.Change(TimeSpan.Zero, UsageInterval);
        _accountTimer.Change(TimeSpan.Zero, AccountInterval);
        _logTimer.Change(TimeSpan.FromSeconds(1), LogPollInterval);
        RaiseChanged();
    }

    private TimeSpan UsageInterval => TimeSpan.FromMinutes(Settings.RefreshIntervalMinutes);

    /// <summary>利用枠・アカウント・ログをすべて今すぐ更新する。</summary>
    public void RefreshAll()
    {
        _ = RefreshUsageAsync();
        _ = RefreshAccountAsync();
        _ = ScanLogsAsync();
    }

    public async Task RefreshUsageAsync()
    {
        if (Interlocked.Exchange(ref _fetching, 1) == 1)
            return;

        _lastFetchStarted = DateTimeOffset.Now;
        RaiseChanged();
        try
        {
            var snapshot = await _cli.FetchUsageAsync(_shutdown.Token).ConfigureAwait(false);
            snapshot.Account = Account;
            Snapshot = snapshot;
            Error = null;
            ClaudeNotFound = false;
            JsonStore.Save(AppPaths.RateLimitCacheFile, snapshot);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ClaudeNotFoundException ex)
        {
            ClaudeNotFound = true;
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            ClaudeNotFound = false;
            Error = FirstLine(ex.Message);
            if (ex is not CliOutputException)
                AppLog.Error("利用枠の取得に失敗しました", ex);
        }
        finally
        {
            Volatile.Write(ref _fetching, 0);
            RaiseChanged();
        }
    }

    public async Task RefreshAccountAsync()
    {
        try
        {
            Account = await _cli.FetchAccountAsync(_shutdown.Token).ConfigureAwait(false);
            if (Snapshot is { } snapshot)
            {
                snapshot.Account = Account;
                JsonStore.Save(AppPaths.RateLimitCacheFile, snapshot);
            }
            RaiseChanged();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Warn($"アカウント情報の取得に失敗しました: {FirstLine(ex.Message)}");
        }
    }

    public async Task ScanLogsAsync()
    {
        try
        {
            await _collector.ScanAsync(_shutdown.Token).ConfigureAwait(false);
            UpdateStats();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error("ログの集計に失敗しました", ex);
        }
    }

    public void ApplySettings(AppSettings settings)
    {
        settings.Normalize();
        lock (_settingsGate)
        {
            // 設定画面で編集しない内部状態は引き継ぐ
            foreach (var (distro, home) in Settings.WslHomes)
                settings.WslHomes.TryAdd(distro, home);
            Settings = settings;
        }
        SaveSettings();
        _cli.InvalidateDetection();
        _usageTimer.Change(TimeSpan.Zero, UsageInterval);
        _ = ScanLogsAsync();
    }

    public void SaveSettings()
    {
        lock (_settingsGate)
            JsonStore.Save(AppPaths.SettingsFile, Settings);
    }

    private async Task<IReadOnlyList<LogSource>> ResolveLogSourcesAsync(CancellationToken ct)
    {
        var (sources, changed) = await LogSourceResolver.ResolveAsync(Settings, ct).ConfigureAwait(false);
        if (changed)
            SaveSettings();
        return sources;
    }

    private void OnRecordsAdded(int count)
    {
        UpdateStats();
        // Claude Code の利用があったら利用枠も更新する（短時間に連続しないよう間隔を空ける）
        if (DateTimeOffset.Now - _lastFetchStarted >= MinFetchIntervalOnActivity)
            _ = RefreshUsageAsync();
    }

    private void UpdateStats()
    {
        try
        {
            Stats = UsageStatsCalculator.Compute(_repository, DateTimeOffset.Now);
            RaiseChanged();
        }
        catch (Exception ex)
        {
            AppLog.Error("集計値の計算に失敗しました", ex);
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            // 復帰直後はネットワークが不安定なため少し待つ
            _usageTimer.Change(TimeSpan.FromSeconds(10), UsageInterval);
        }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
            _usageTimer.Change(TimeSpan.FromSeconds(5), UsageInterval);
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Error("画面の更新に失敗しました", ex);
        }
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? text;

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _shutdown.Cancel();
        _usageTimer.Dispose();
        _logTimer.Dispose();
        _accountTimer.Dispose();
        _collector.Dispose();
        _repository.Dispose();
    }
}
