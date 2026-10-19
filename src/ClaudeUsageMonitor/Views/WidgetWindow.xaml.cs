using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ClaudeUsageMonitor.Core.Settings;
using ClaudeUsageMonitor.Services;
using ClaudeUsageMonitor.ViewModels;

namespace ClaudeUsageMonitor.Views;

public partial class WidgetWindow : Window
{
    private const double CompactWidth = 300;
    private const double DetailWidth = 340;
    private const double SnapDistance = 16;

    private readonly WidgetViewModel _viewModel;
    private IntPtr _hwnd;

    public WidgetWindow(WidgetViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    public event Action? RefreshRequested;
    public event Action? SettingsRequested;
    public event Action? HideRequested;
    public event Action? DetailToggled;
    public event Action? PositionChanged;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        // Alt+Tab に表示しない
        NativeMethods.SetExStyle(_hwnd, NativeMethods.WS_EX_TOOLWINDOW, true);
        NativeMethods.SetExStyle(_hwnd, NativeMethods.WS_EX_APPWINDOW, false);
    }

    public void ApplySettings(AppSettings settings)
    {
        Opacity = settings.OpacityPercent / 100.0;
        Topmost = settings.Topmost == TopmostMode.AlwaysOnTop;
        if (settings.Topmost == TopmostMode.Bottom && _hwnd != IntPtr.Zero)
            NativeMethods.SendToBottom(_hwnd);
        SetClickThrough(settings.ClickThrough);
        SetDetail(settings.DetailMode);
        _bottomMode = settings.Topmost == TopmostMode.Bottom;
    }

    private bool _bottomMode;

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (_bottomMode && _hwnd != IntPtr.Zero)
            NativeMethods.SendToBottom(_hwnd);
    }

    public void SetClickThrough(bool enabled)
    {
        if (_hwnd != IntPtr.Zero)
            NativeMethods.SetExStyle(_hwnd, NativeMethods.WS_EX_TRANSPARENT, enabled);
    }

    public void SetDetail(bool detail)
    {
        _viewModel.IsDetail = detail;
        Width = detail ? DetailWidth : CompactWidth;
        DetailButton.Content = detail ? "" : "";
    }

    /// <summary>保存位置に復元する。画面外になる場合はプライマリモニターの右下に置く。</summary>
    public void RestorePosition(double? left, double? top)
    {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        if (left is { } l && top is { } t
            && l + 40 > virtualLeft && l + 40 < virtualRight
            && t + 20 > virtualTop && t + 20 < virtualBottom)
        {
            Left = l;
            Top = t;
            return;
        }

        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 12;
        Top = workArea.Bottom - 320;

        // 実際の高さが決まってから右下に揃える
        void PlaceBottomRight(object? sender, EventArgs e)
        {
            ContentRendered -= PlaceBottomRight;
            if (GetWorkArea() is { } area)
            {
                Left = area.Right - ActualWidth;
                Top = area.Bottom - ActualHeight;
            }
        }
        ContentRendered += PlaceBottomRight;
    }

    // ボタン以外の部分を押した位置。一定距離動いたらドラッグ、動かずに離したらクリックとして扱う
    private Point? _pressPoint;

    private void OnRootMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressPoint = e.GetPosition(this);
        RootBorder.CaptureMouse();
        e.Handled = true;
    }

    private void OnRootMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressPoint is not { } start || e.LeftButton != MouseButtonState.Pressed)
            return;

        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _pressPoint = null;
        RootBorder.ReleaseMouseCapture();
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        SnapToEdges();
        PositionChanged?.Invoke();
    }

    private void OnRootMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressPoint is null)
            return;
        _pressPoint = null;
        RootBorder.ReleaseMouseCapture();
        DetailToggled?.Invoke();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (!IsVisible || GetWorkArea() is not { } area)
            return;

        // 画面の右端・下端に寄せていた場合は、サイズが変わってもその端に揃える
        var old = sizeInfo.PreviousSize;
        if (old.Width > 0 && Math.Abs(Left + old.Width - area.Right) <= SnapDistance)
            Left = area.Right - ActualWidth;
        if (old.Height > 0 && Math.Abs(Top + old.Height - area.Bottom) <= SnapDistance)
            Top = area.Bottom - ActualHeight;

        // 詳細表示への切り替えなどで大きくなったときに画面外へはみ出さないようにする
        KeepInsideWorkArea();
    }

    private void KeepInsideWorkArea()
    {
        if (GetWorkArea() is not { } area)
            return;

        if (Left + ActualWidth > area.Right)
            Left = Math.Max(area.Left, area.Right - ActualWidth);
        if (Top + ActualHeight > area.Bottom)
            Top = Math.Max(area.Top, area.Bottom - ActualHeight);
        if (Left < area.Left)
            Left = area.Left;
        if (Top < area.Top)
            Top = area.Top;
    }

    /// <summary>ウィンドウがあるモニターの作業領域（DIP）。</summary>
    private Rect? GetWorkArea()
    {
        if (_hwnd == IntPtr.Zero)
            return null;

        var screen = System.Windows.Forms.Screen.FromHandle(_hwnd);
        var dpi = VisualTreeHelper.GetDpi(this);
        var area = screen.WorkingArea;
        return new Rect(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY,
            area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY);
    }

    /// <summary>作業領域の端に近ければ吸着させる。</summary>
    private void SnapToEdges()
    {
        if (GetWorkArea() is not { } area)
            return;

        double areaLeft = area.Left, areaTop = area.Top, areaRight = area.Right, areaBottom = area.Bottom;

        // Border の Margin（影の分）を除いた見た目の端で判定する
        var margin = RootBorder.Margin;
        var visualLeft = Left + margin.Left;
        var visualTop = Top + margin.Top;
        var visualRight = Left + ActualWidth - margin.Right;
        var visualBottom = Top + ActualHeight - margin.Bottom;

        if (Math.Abs(visualLeft - areaLeft) < SnapDistance) Left = areaLeft - margin.Left;
        else if (Math.Abs(visualRight - areaRight) < SnapDistance) Left = areaRight - ActualWidth + margin.Right;

        if (Math.Abs(visualTop - areaTop) < SnapDistance) Top = areaTop - margin.Top;
        else if (Math.Abs(visualBottom - areaBottom) < SnapDistance) Top = areaBottom - ActualHeight + margin.Bottom;
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();
    private void OnToggleDetailClick(object sender, RoutedEventArgs e) => DetailToggled?.Invoke();
    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void OnHideClick(object sender, RoutedEventArgs e) => HideRequested?.Invoke();
}
