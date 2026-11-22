using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StepRecorder.Core;
using static StepRecorder.App.Tests.Desktop;

namespace StepRecorder.App.Tests;

/// <summary>
/// モニターの撮影と録画。実際の画面が写らないよう、モニター全面を一色のウィンドウで覆ってから撮る。入力は送らない。
/// </summary>
public sealed class MonitorTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "sr-monitor-" + Guid.NewGuid());
    Process? sample;

    public void Dispose()
    {
        try { sample?.Kill(); } catch (InvalidOperationException) { }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    static ColorWindow Cover(DisplayMonitor m, Color color) => new(color, m.Bounds.X, m.Bounds.Y, m.Bounds.Width, m.Bounds.Height);

    static void AssertColor(Color expected, Color actual, string where) =>
        Assert.True(Math.Abs(expected.R - actual.R) <= 6 && Math.Abs(expected.G - actual.G) <= 6 && Math.Abs(expected.B - actual.B) <= 6,
            $"{where}: {expected} が {actual} になった");

    [DesktopFact]
    public void どのモニターも_位置と色のとおりに撮れて録れる()
    {
        var color = Color.FromRgb(230, 40, 90); // 係数や範囲を取り違えるとずれる色
        foreach (var m in Monitors.All())
        {
            using var cover = Cover(m, color);
            var bounds = Native.ToRect(m.Bounds);

            var shot = WindowCapture.CaptureMonitor(bounds, cover.Handle);
            Assert.Equal((m.Bounds.Width, m.Bounds.Height), (shot.Image.PixelWidth, shot.Image.PixelHeight));

            var path = Path.Combine(dir, $"{m.Name}.mp4");
            var rec = VideoRecorder.StartMonitor(m.Handle, bounds, path, cursor: false);
            Wait(1200);
            rec.Stop();
            var frame = VideoClips.Frame(path, TimeSpan.FromSeconds(0.6));
            Assert.Equal((m.Bounds.Width & ~1, m.Bounds.Height & ~1), (frame.PixelWidth, frame.PixelHeight));

            foreach (var (kind, image) in new[] { ("静止画", (BitmapSource)shot.Image), ("動画", frame) })
                foreach (var (x, y) in new[] { (30, 30), (image.PixelWidth / 2, image.PixelHeight / 2), (image.PixelWidth - 30, image.PixelHeight - 30) })
                    AssertColor(color, VideoE2ETests.ColorAt(image, x, y), $"{m.Label} の{kind} ({x},{y})");
        }
    }

    [DesktopFact]
    public void 操作したウィンドウの範囲がトリミングになる()
    {
        var m = Monitors.All().Single(x => x.Primary);
        using var cover = Cover(m, Colors.Blue);
        using var window = new ColorWindow(Colors.Lime, m.Bounds.X + 200, m.Bounds.Y + 150, 500, 300);

        var shot = WindowCapture.CaptureMonitor(Native.ToRect(m.Bounds), window.Handle);
        var frame = Native.FrameBounds(window.Handle);

        Assert.Equal(new PixelRect(frame.Left - m.Bounds.X, frame.Top - m.Bounds.Y, frame.Width, frame.Height), shot.Crop);
        AssertColor(Colors.Lime, VideoE2ETests.Center(new CroppedBitmap(shot.Image, new(shot.Crop!.X, shot.Crop.Y, shot.Crop.Width, shot.Crop.Height))), "トリミングの中");
        Assert.Null(WindowCapture.CaptureMonitor(Native.ToRect(m.Bounds), 0).Crop);
    }

    [DesktopFact]
    public void 除外したウィンドウは静止画にも動画にも写らない()
    {
        var m = Monitors.All().Single(x => x.Primary);
        var bounds = Native.ToRect(m.Bounds);
        using var cover = Cover(m, Colors.Blue);
        using var bar = new ColorWindow(Colors.Red, m.Bounds.X + 200, m.Bounds.Y + 150, 500, 300);
        var (x, y) = (450, 300); // bar の真ん中
        AssertColor(Colors.Red, VideoE2ETests.ColorAt(WindowCapture.CaptureMonitor(bounds, 0).Image, x, y), "除外する前");

        bar.Invoke(_ => Native.ExcludeFromCapture(bar.Handle));
        Wait(300);

        AssertColor(Colors.Blue, VideoE2ETests.ColorAt(WindowCapture.CaptureMonitor(bounds, 0).Image, x, y), "静止画");
        var path = Path.Combine(dir, "excluded.mp4");
        var rec = VideoRecorder.StartMonitor(m.Handle, bounds, path, cursor: false);
        Wait(1000);
        rec.Stop();
        AssertColor(Colors.Blue, VideoE2ETests.ColorAt(VideoClips.Frame(path, TimeSpan.FromSeconds(0.5)), x, y), "動画");
    }

    [DesktopFact]
    public void そのモニターの上の_他のアプリへの操作だけを拾う()
    {
        sample = Open(SampleExe);
        var window = sample.MainWindowHandle;
        var f = Native.FrameBounds(window);
        var center = new Native.POINT { X = (f.Left + f.Right) / 2, Y = (f.Top + f.Bottom) / 2 };
        var on = Native.MonitorFromWindow(window, Native.MONITOR_DEFAULTTONEAREST);

        foreach (var m in Monitors.All())
        {
            var target = new MonitorTarget(m);
            Assert.True(target.IsAvailable);
            Assert.Equal(m.Handle == on, target.OwnsKey(window));
            Assert.Equal(m.Handle == on, target.OwnsClick(window, center));
        }

        using var own = new ColorWindow(Colors.Gray);
        var ownAt = new Native.POINT { X = 300, Y = 250 };
        var main = new MonitorTarget(Monitors.All().Single(x => x.Primary));
        Assert.False(main.OwnsKey(own.Handle)); // 自分のウィンドウへの操作は拾わない
        Assert.False(main.OwnsClick(own.Handle, ownAt));
    }

    [DesktopFact]
    public void ウィンドウの情報とエビデンス()
    {
        sample = Open(SampleExe);
        var m = Monitors.All().Single(x => x.Primary);
        var target = new MonitorTarget(m);

        var info = target.Describe(sample.MainWindowHandle);
        Assert.Equal(("サンプル画面", "SampleTarget"), (info.Title, info.ProcessName));
        Assert.Equal(new WindowInfo(m.Title, "", ""), target.Describe(0));

        var evidence = target.CollectEvidence();
        Assert.Equal(("", ""), (evidence.AppFile, evidence.AppVersion));
        Assert.Equal($"{m.Bounds.Width}×{m.Bounds.Height}", evidence.Screen);
        Assert.True(evidence.ScalePercent >= 100);
    }
}
