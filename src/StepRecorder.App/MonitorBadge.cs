using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using StepRecorder.Core;
using static StepRecorder.App.Native;

namespace StepRecorder.App;

/// <summary>どのモニターか分かるように、赤枠と番号を少しの間だけ出す。クリックは下へ通し、前面も奪わず、取り込みにも写らない。</summary>
sealed class MonitorBadge : Window
{
    static readonly TimeSpan Shown = TimeSpan.FromSeconds(1.5);

    readonly PixelRect bounds;
    readonly DispatcherTimer timer = new() { Interval = Shown };

    public MonitorBadge(DisplayMonitor monitor)
    {
        bounds = monitor.Bounds;
        Title = $"step_recorder {monitor.Title}";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Yu Gothic UI");

        var red = new SolidColorBrush(Color.FromRgb(0xE0, 0x40, 0x40));
        Content = new Border
        {
            BorderBrush = red,
            BorderThickness = new Thickness(10),
            Child = new Border
            {
                Background = red,
                Padding = new Thickness(32, 12, 32, 12),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = monitor.Title, FontSize = 72, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
            },
        };

        timer.Tick += (_, _) => Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)((long)GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT));
        ExcludeFromCapture(hwnd);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // Left/Top は拡大率の違うモニターの間でずれるので物理ピクセルで置く。移ると大きさが変わるので 2 回
        var hwnd = new WindowInteropHelper(this).Handle;
        for (var i = 0; i < 2; i++) SetWindowPos(hwnd, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        timer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        timer.Stop();
        base.OnClosed(e);
    }
}
