using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using ClaudeUsageMonitor.Core.Models;
using ClaudeUsageMonitor.Core.Settings;
using ClaudeUsageMonitor.ViewModels;

namespace ClaudeUsageMonitor.Services;

/// <summary>タスクトレイアイコン。Session (5hr) の使用率を数字と色で表示する。</summary>
public sealed class TrayIconManager : IDisposable
{
    private const int NotifyIconTextLimit = 127;

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _toggleWidgetItem;
    private readonly ToolStripMenuItem _detailItem;
    private readonly ToolStripMenuItem _clickThroughItem;
    private string? _iconKey;
    private Icon? _currentIcon;

    public TrayIconManager()
    {
        _toggleWidgetItem = new ToolStripMenuItem("ウィジェットを表示", null, (_, _) => ToggleWidgetRequested?.Invoke());
        _detailItem = new ToolStripMenuItem("詳細表示", null, (_, _) => ToggleDetailRequested?.Invoke());
        _clickThroughItem = new ToolStripMenuItem("クリック透過", null, (_, _) => ToggleClickThroughRequested?.Invoke());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            _toggleWidgetItem,
            _detailItem,
            _clickThroughItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("今すぐ更新", null, (_, _) => RefreshRequested?.Invoke()),
            new ToolStripMenuItem("設定...", null, (_, _) => SettingsRequested?.Invoke()),
            new ToolStripMenuItem("ログフォルダを開く", null, (_, _) => OpenLogsRequested?.Invoke()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("終了", null, (_, _) => ExitRequested?.Invoke()),
        ]);

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Text = "Claude Usage Monitor",
            Visible = true,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ToggleWidgetRequested?.Invoke();
        };
        Update(null, new AppSettings(), DateTimeOffset.Now, widgetVisible: true);
    }

    public event Action? ToggleWidgetRequested;
    public event Action? ToggleDetailRequested;
    public event Action? ToggleClickThroughRequested;
    public event Action? RefreshRequested;
    public event Action? SettingsRequested;
    public event Action? OpenLogsRequested;
    public event Action? ExitRequested;

    public void Update(RateLimitSnapshot? snapshot, AppSettings settings, DateTimeOffset now, bool widgetVisible, string? error = null)
    {
        _toggleWidgetItem.Text = widgetVisible ? "ウィジェットを隠す" : "ウィジェットを表示";
        _detailItem.Checked = settings.DetailMode;
        _clickThroughItem.Checked = settings.ClickThrough;

        var primary = UsageLevels.Primary(snapshot);
        var stale = snapshot is null || now - snapshot.FetchedAt > WidgetViewModel.StaleAfter;
        var resetPassed = primary?.ResetsAt is { } r && r <= now;
        var percent = primary is null ? (double?)null : resetPassed ? 0 : primary.UsedPercent;
        var level = percent is null || stale || resetPassed ? UsageLevel.Stale : UsageLevels.Of(percent.Value, settings);
        SetIcon(percent, level);

        _notifyIcon.Text = Truncate(BuildTooltip(snapshot, now, error));
    }

    public void ShowNotification(string title, string text, bool warning)
    {
        _notifyIcon.ShowBalloonTip(10_000, title, text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    private static string BuildTooltip(RateLimitSnapshot? snapshot, DateTimeOffset now, string? error)
    {
        if (snapshot is null || snapshot.Limits.Count == 0)
            return error is null ? "Claude Usage Monitor" : $"Claude Usage Monitor\n{error}";

        var lines = snapshot.Limits.Select(l =>
        {
            var reset = l.ResetsAt is { } at ? Format.ResetsIn(at, now)?.Replace("Resets in ", "") ?? "リセット済" : "";
            return $"{l.Label}: {Format.Percent(l.UsedPercent)}{(reset.Length > 0 ? $" ({reset})" : "")}";
        });
        return string.Join("\n", lines);
    }

    private static string Truncate(string text) =>
        text.Length <= NotifyIconTextLimit ? text : text[..(NotifyIconTextLimit - 1)] + "…";

    private void SetIcon(double? percent, UsageLevel level)
    {
        var key = $"{percent?.ToString("0") ?? "-"}|{level}|{ThemeManager.IsDark}";
        if (key == _iconKey)
            return;
        _iconKey = key;

        var icon = RenderIcon(percent, level);
        _notifyIcon.Icon = icon;
        if (_currentIcon is not null)
        {
            NativeMethods.DestroyIcon(_currentIcon.Handle);
            _currentIcon.Dispose();
        }
        _currentIcon = icon;
    }

    private static Icon RenderIcon(double? percent, UsageLevel level)
    {
        const int size = 32;
        var background = level switch
        {
            UsageLevel.Critical => Color.FromArgb(0xE5, 0x53, 0x4B),
            UsageLevel.Warning => Color.FromArgb(0xE5, 0xB3, 0x3A),
            UsageLevel.Stale => Color.FromArgb(0x70, 0x70, 0x70),
            _ => Color.FromArgb(0xD9, 0x77, 0x57),
        };
        var text = percent is null ? "–" : Math.Min(100, Math.Round(percent.Value)).ToString("0");

        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using (var path = RoundedRect(new Rectangle(0, 0, size - 1, size - 1), 7))
            using (var brush = new SolidBrush(background))
                g.FillPath(brush, path);

            var fontSize = text.Length >= 3 ? 12f : 17f;
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 1, size, size), format);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        if (_currentIcon is not null)
        {
            NativeMethods.DestroyIcon(_currentIcon.Handle);
            _currentIcon.Dispose();
        }
    }
}
