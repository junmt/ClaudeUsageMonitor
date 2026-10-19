using ClaudeUsageMonitor.Core.Infrastructure;
using ClaudeUsageMonitor.Core.Models;
using ClaudeUsageMonitor.Core.Settings;
using ClaudeUsageMonitor.ViewModels;

namespace ClaudeUsageMonitor.Services;

/// <summary>
/// しきい値超過・リセットの通知。同じ枠・同じリセット周期・同じしきい値の通知は1回だけ出す。
/// </summary>
public sealed class NotificationManager(Action<string, string, bool> show)
{
    private const int MaxRememberedKeys = 200;

    private sealed class State
    {
        public List<string> NotifiedKeys { get; set; } = [];

        /// <summary>枠ごとの前回のリセット時刻（リセット検知用）。</summary>
        public Dictionary<string, DateTimeOffset> LastResets { get; set; } = [];
    }

    private readonly State _state = JsonStore.Load<State>(AppPaths.StateFile) ?? new State();
    private DateTimeOffset? _lastProcessed;

    public void Process(RateLimitSnapshot? snapshot, AppSettings settings)
    {
        if (snapshot is null || snapshot.FetchedAt == _lastProcessed)
            return;
        _lastProcessed = snapshot.FetchedAt;

        var changed = false;
        foreach (var limit in snapshot.Limits)
        {
            // 取得元によってリセット時刻の丸めが異なる（8pm / 7:59pm）ため、最も近い正時で周期を識別する
            var period = limit.ResetsAt?.ToUniversalTime().AddMinutes(30).ToString("yyyyMMddHH") ?? "none";

            if (settings.NotificationsEnabled)
            {
                var level = UsageLevels.Of(limit.UsedPercent, settings);
                if (level is UsageLevel.Warning or UsageLevel.Critical)
                {
                    var key = $"{limit.RawLabel}|{period}|{level}";
                    if (!_state.NotifiedKeys.Contains(key))
                    {
                        _state.NotifiedKeys.Add(key);
                        // 危険を通知したら、同じ周期の警告は出さない
                        if (level == UsageLevel.Critical)
                            _state.NotifiedKeys.Add($"{limit.RawLabel}|{period}|{UsageLevel.Warning}");
                        changed = true;

                        var threshold = level == UsageLevel.Critical ? settings.CriticalThreshold : settings.WarningThreshold;
                        var reset = limit.ResetsAt is { } at ? $"\nリセット: {Format.DateTime(at)}" : "";
                        show($"{limit.Label} が {Format.Percent(limit.UsedPercent)} に達しました",
                            $"{(level == UsageLevel.Critical ? "危険" : "警告")}しきい値（{threshold}%）を超えました。{reset}", true);
                    }
                }
            }

            if (limit.ResetsAt is { } resetsAt)
            {
                if (_state.LastResets.TryGetValue(limit.RawLabel, out var previous)
                    && resetsAt > previous.AddMinutes(30)
                    && settings.NotificationsEnabled && settings.NotifyOnReset)
                {
                    show($"{limit.Label} がリセットされました", $"現在の使用率: {Format.Percent(limit.UsedPercent)}", false);
                }
                if (!_state.LastResets.TryGetValue(limit.RawLabel, out var stored) || stored != resetsAt)
                {
                    _state.LastResets[limit.RawLabel] = resetsAt;
                    changed = true;
                }
            }
        }

        if (!changed)
            return;
        if (_state.NotifiedKeys.Count > MaxRememberedKeys)
            _state.NotifiedKeys.RemoveRange(0, _state.NotifiedKeys.Count - MaxRememberedKeys);
        JsonStore.Save(AppPaths.StateFile, _state);
    }
}
