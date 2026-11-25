using System.Diagnostics;
using System.Windows.Media;
using static StepRecorder.App.Tests.Desktop;

namespace StepRecorder.App.Tests;

/// <summary>アプリの静止画。入力は送らない。</summary>
public sealed class WindowCaptureTests : IDisposable
{
    Process? sample;

    public void Dispose()
    {
        try { sample?.Kill(); } catch (InvalidOperationException) { }
    }

    /// <summary>
    /// DPI 非対応のアプリは 96 DPI で描き、拡大率が 100% でないモニターでは DWM が伸ばして見せている。
    /// PrintWindow はその伸ばす前の絵を返すので、そのまま貼ると右下が欠ける(システム DPI 対応のアプリを別の拡大率のモニターに置いたときも同じ)。
    /// </summary>
    [DesktopFact]
    public void 描くDPIとモニターのDPIが違うアプリも_ウィンドウ全体が写る()
    {
        var m = Monitors.All().Single(x => x.Primary);
        if (Monitors.ScalePercent(m.Handle) == 100) return; // ponytail: 拡大率 100% のメイン画面では確かめられない(xunit 2 は途中で Skip にできない)

        Environment.SetEnvironmentVariable("SAMPLE_DPI_UNAWARE", "1");
        try { sample = Open(SampleExe, "Lime"); }
        finally { Environment.SetEnvironmentVariable("SAMPLE_DPI_UNAWARE", null); }
        var hwnd = sample.MainWindowHandle;
        ColorWindow.Raise(hwnd);
        Wait(500);

        var shot = WindowCapture.Capture(hwnd)!;
        var frame = Native.FrameBounds(hwnd);
        Assert.Equal((frame.Width, frame.Height), (shot.Image.PixelWidth, shot.Image.PixelHeight));
        // タブの中は Lime。右下が欠けていれば、下の方は Lime にならない
        foreach (var (fx, fy) in new[] { (0.1, 0.5), (0.1, 0.85), (0.5, 0.85) })
        {
            var (x, y) = ((int)(shot.Image.PixelWidth * fx), (int)(shot.Image.PixelHeight * fy));
            var c = VideoE2ETests.ColorAt(shot.Image, x, y);
            Assert.True(Math.Abs(c.R - Colors.Lime.R) <= 6 && Math.Abs(c.G - Colors.Lime.G) <= 6 && Math.Abs(c.B - Colors.Lime.B) <= 6, $"({x},{y}) が {c}");
        }
    }
}
