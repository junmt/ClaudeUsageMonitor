using System.Collections.ObjectModel;
using ClaudeUsageMonitor.Core.Models;
using ClaudeUsageMonitor.Core.Settings;
using ClaudeUsageMonitor.Services;

namespace ClaudeUsageMonitor.ViewModels;

public sealed class WidgetViewModel : ObservableObject
{
    /// <summary>この時間以上更新されていない値は古いとみなして灰色表示する。</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    private string? _email;
    private bool _showAccount;
    private string? _accountTooltip;
    private bool _hasLimits;
    private string? _errorMessage;
    private string? _noticeMessage;
    private string? _rawText;
    private string _footerText = "";
    private bool _isStale;
    private bool _isRefreshing;
    private bool _isDetail;
    private bool _hasLocalStats;
    private string _blockText = "";
    private string _burnRateText = "";
    private string _todayText = "";
    private string _weekText = "";
    private string _monthText = "";
    private string _todayBreakdown = "";
    private string? _insights;

    public ObservableCollection<LimitItemViewModel> Limits { get; } = [];
    public ObservableCollection<BarItemViewModel> Models { get; } = [];
    public ObservableCollection<BarItemViewModel> Projects { get; } = [];

    public string? Email { get => _email; private set => SetProperty(ref _email, value); }
    public bool ShowAccount { get => _showAccount; private set => SetProperty(ref _showAccount, value); }
    public string? AccountTooltip { get => _accountTooltip; private set => SetProperty(ref _accountTooltip, value); }
    public bool HasLimits { get => _hasLimits; private set => SetProperty(ref _hasLimits, value); }
    public string? ErrorMessage { get => _errorMessage; private set { if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public string? NoticeMessage { get => _noticeMessage; private set { if (SetProperty(ref _noticeMessage, value)) OnPropertyChanged(nameof(HasNotice)); } }
    public bool HasNotice => !string.IsNullOrEmpty(NoticeMessage);

    /// <summary>利用枠を1行も解析できなかったときの生テキスト。</summary>
    public string? RawText { get => _rawText; private set { if (SetProperty(ref _rawText, value)) OnPropertyChanged(nameof(HasRawText)); } }
    public bool HasRawText => !string.IsNullOrEmpty(RawText);

    public string FooterText { get => _footerText; private set => SetProperty(ref _footerText, value); }
    public bool IsStale { get => _isStale; private set => SetProperty(ref _isStale, value); }
    public bool IsRefreshing { get => _isRefreshing; private set => SetProperty(ref _isRefreshing, value); }
    public bool IsDetail { get => _isDetail; set => SetProperty(ref _isDetail, value); }

    public bool HasLocalStats { get => _hasLocalStats; private set => SetProperty(ref _hasLocalStats, value); }
    public string BlockText { get => _blockText; private set => SetProperty(ref _blockText, value); }
    public string BurnRateText { get => _burnRateText; private set => SetProperty(ref _burnRateText, value); }
    public string TodayText { get => _todayText; private set => SetProperty(ref _todayText, value); }
    public string WeekText { get => _weekText; private set => SetProperty(ref _weekText, value); }
    public string MonthText { get => _monthText; private set => SetProperty(ref _monthText, value); }
    public string TodayBreakdown { get => _todayBreakdown; private set => SetProperty(ref _todayBreakdown, value); }
    public string? Insights { get => _insights; private set { if (SetProperty(ref _insights, value)) OnPropertyChanged(nameof(HasInsights)); } }
    public bool HasInsights => !string.IsNullOrEmpty(Insights);

    public void Update(MonitorController controller, DateTimeOffset now)
    {
        var settings = controller.Settings;
        var snapshot = controller.Snapshot;
        var account = controller.Account ?? snapshot?.Account;

        UpdateAccount(account, settings);

        var stale = snapshot is null || now - snapshot.FetchedAt > StaleAfter;
        IsStale = snapshot is not null && stale;
        IsRefreshing = controller.IsFetching;

        UpdateLimits(snapshot, settings, stale, now);

        ErrorMessage = controller.Error;
        NoticeMessage = account is { LoggedIn: false }
            ? "Claude Code にログインしてください（claude を起動して /login）。"
            : snapshot is { UsingSubscription: false }
                ? "サブスクリプションで利用していません。"
                : null;
        RawText = snapshot is not null && snapshot.Limits.Count == 0 && controller.Error is null ? snapshot.RawResult.Trim() : null;
        Insights = snapshot?.Insights;

        FooterText = snapshot is null
            ? (controller.IsFetching ? "取得中…" : "未取得")
            : stale
                ? $"最終取得：{Format.Ago(now - snapshot.FetchedAt)}"
                : $"更新 {snapshot.FetchedAt.ToLocalTime():HH:mm}";

        UpdateStats(controller.Stats, now);
    }

    private void UpdateAccount(AccountInfo? account, AppSettings settings)
    {
        Email = account?.Email;
        ShowAccount = settings.ShowEmail && !string.IsNullOrEmpty(account?.Email);
        var details = new[] { account?.OrgName, account?.SubscriptionType is { } plan ? $"プラン: {plan}" : null }
            .Where(s => !string.IsNullOrEmpty(s));
        AccountTooltip = string.Join(Environment.NewLine, details) is { Length: > 0 } text ? text : null;
    }

    private void UpdateLimits(RateLimitSnapshot? snapshot, AppSettings settings, bool stale, DateTimeOffset now)
    {
        var items = new List<LimitItemViewModel>();
        foreach (var limit in snapshot?.Limits ?? [])
        {
            var resetPassed = limit.ResetsAt is { } r && r <= now;
            var percent = resetPassed ? 0 : limit.UsedPercent;
            var level = stale || resetPassed ? UsageLevel.Stale : UsageLevels.Of(percent, settings);

            var resetText = limit.ResetsAt switch
            {
                null => limit.ResetText is null ? "" : $"Resets {limit.ResetText}",
                { } at when resetPassed => "リセット済み（次回の取得で更新）",
                { } at => Format.ResetsIn(at, now) ?? "",
            };
            var tooltip = limit.ResetsAt is { } abs ? $"リセット: {Format.DateTime(abs)}" : null;

            items.Add(new LimitItemViewModel(limit.Label, Math.Clamp(percent, 0, 100), Format.Percent(percent), resetText, tooltip, level));
        }

        if (!items.SequenceEqual(Limits))
        {
            Limits.Clear();
            foreach (var item in items)
                Limits.Add(item);
        }
        HasLimits = items.Count > 0;
    }

    private void UpdateStats(UsageStats? stats, DateTimeOffset now)
    {
        HasLocalStats = stats is not null && stats.Month.Total > 0;
        if (stats is null)
            return;

        if (stats.CurrentBlock is { } block)
        {
            var remaining = block.End - now;
            BlockText = $"{Format.Tokens(block.Tokens.Total)} tok（残り {(int)remaining.TotalHours}h{remaining.Minutes:00}m）";
            BurnRateText = $"{Format.Tokens((long)block.BurnRatePerMinute)} tok/分 ・ 終了時予測 {Format.Tokens(block.ProjectedTotal)}";
        }
        else
        {
            BlockText = "アクティブなブロックなし";
            BurnRateText = "";
        }

        TodayText = Format.Tokens(stats.Today.Total);
        WeekText = Format.Tokens(stats.Week.Total);
        MonthText = Format.Tokens(stats.Month.Total);
        TodayBreakdown = $"入力 {Format.Tokens(stats.Today.Input)} ・ 出力 {Format.Tokens(stats.Today.Output)} ・ "
                         + $"キャッシュ書込 {Format.Tokens(stats.Today.CacheWrite)} ・ 読込 {Format.Tokens(stats.Today.CacheRead)}";

        Replace(Models, ToBars(stats.ModelsThisWeek, n => n, n => null));
        Replace(Projects, ToBars(stats.ProjectsThisWeek, Format.ProjectName, n => n));
    }

    private static List<BarItemViewModel> ToBars(IReadOnlyList<NamedTotal> totals, Func<string, string> name, Func<string, string?> tooltip)
    {
        var max = totals.Count == 0 ? 1 : Math.Max(1, totals.Max(t => t.Tokens));
        return totals.Select(t => new BarItemViewModel(name(t.Name), Format.Tokens(t.Tokens), (double)t.Tokens / max, tooltip(t.Name))).ToList();
    }

    private static void Replace<T>(ObservableCollection<T> target, List<T> items)
    {
        if (items.SequenceEqual(target))
            return;
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }
}
