using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Threading;
using StepRecorder.Core;
using static StepRecorder.App.Tests.Desktop;

namespace StepRecorder.App.Tests;

/// <summary>記録バーの自動で引っ込む動き。対象はテスト用アプリなので、フックはそのアプリへの操作しか拾わない。</summary>
public sealed class RecordBarTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "sr-bar-" + Guid.NewGuid());
    readonly Process sample;
    Thread? thread;
    RecordBar? bar;
    nint hwnd;

    public RecordBarTests() => sample = Open(SampleExe);

    public void Dispose()
    {
        bar?.Dispatcher.BeginInvoke(bar.Close); // 止め終えてから閉じる
        thread?.Join(10000);
        try { sample.Kill(); } catch (InvalidOperationException) { }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    void Show(RecordMode mode = RecordMode.Click, bool realPointer = false)
    {
        Directory.CreateDirectory(dir);
        // 利用者のホットキーとぶつからない組み合わせ
        var settings = new Settings
        {
            DefaultMode = mode,
            HotkeyCapture = "Ctrl+Alt+Shift+F13", HotkeyPause = "Ctrl+Alt+Shift+F14",
            HotkeyStop = "Ctrl+Alt+Shift+F15", HotkeyClip = "Ctrl+Alt+Shift+F16",
        };
        using var ready = new ManualResetEventSlim();
        thread = new Thread(() =>
        {
            bar = new RecordBar(new RecorderEngine(new AppTarget(sample), dir, settings), settings, dir);
            if (!realPointer) bar.IsPointerOver = () => false;
            bar.ContentRendered += (_, _) =>
            {
                hwnd = new WindowInteropHelper(bar).Handle;
                ready.Set();
            };
            bar.Closed += (_, _) =>
            {
                bar.Progress?.Finish();
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            };
            bar.Show();
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(ready.Wait(10000), "バーが出ない");
        Wait(400);
    }

    T On<T>(Func<RecordBar, T> f) => bar!.Dispatcher.Invoke(() => f(bar));

    Native.RECT Bounds()
    {
        Assert.True(Native.GetWindowRect(hwnd, out var r));
        return r;
    }

    bool IsOpen() => On(b => b.Panel.IsVisible);

    void WaitClosed() => Assert.True(Until(() => !IsOpen(), 5000), "引っ込まない(マウスがバーの上にある?)");

    void PressTab() => ((InvokePattern)Find(hwnd, "BarTab").GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    void Snapshot(string name)
    {
        if (Environment.GetEnvironmentVariable("STEP_RECORDER_E2E_OUT") is { Length: > 0 } outDir)
            WindowCapture.SavePng(WindowCapture.Capture(hwnd)!.Image, Path.Combine(outDir, $"{name}.png"));
    }

    [DesktopFact]
    public void 少しすると画面の端の小さなタブになり_押すと元の所に出る()
    {
        Show();
        Assert.True(IsOpen());
        var open = Bounds();
        Snapshot("bar-open");

        WaitClosed();
        Wait(400);
        var tab = Bounds();
        Snapshot("bar-tab");
        var scale = On(b => PresentationSource.FromVisual(b)!.CompositionTarget.TransformToDevice.M11);
        Assert.True(tab.Width <= 60 * scale && tab.Height <= 18 * scale, $"タブが大きい: {tab.Width}×{tab.Height}");
        Assert.Equal(open.Top, tab.Top);
        var info = Monitors.Info(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST))!.Value;
        Assert.Equal(info.rcWork.Top, tab.Top);
        Assert.InRange((tab.Left + tab.Right) - (open.Left + open.Right), -4, 4);

        PressTab();
        Assert.True(Until(IsOpen, 2000), "出ない");
        Wait(400);
        var again = Bounds();
        Assert.InRange(again.Left - open.Left, -2, 2);
        Assert.InRange(again.Width - open.Width, -2, 2);
        WaitClosed();
    }

    [DesktopFact]
    public void 開閉を繰り返しても位置がずれない()
    {
        Show(RecordMode.Video);
        WaitClosed();
        Wait(300);
        var first = Bounds();
        for (var i = 0; i < 3; i++)
        {
            PressTab();
            Assert.True(Until(IsOpen, 2000), "出ない");
            WaitClosed();
            Wait(300);
        }
        Assert.InRange(Bounds().Left - first.Left, -1, 1);
    }

    [DesktopFact]
    public void 文言で幅が変わってもタブの位置がずれない()
    {
        Show();
        WaitClosed();
        Wait(300);
        var first = Bounds().Left;
        void TogglePause() => On(b =>
        {
            b.PauseButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            return true;
        });

        for (var i = 0; i < 2; i++)
        {
            TogglePause(); // ホットキーで(引っ込んだまま)止めて
            PressTab();
            Assert.True(Until(IsOpen, 2000), "出ない");
            TogglePause(); // 出したバーで再開
            WaitClosed();
            Wait(300);
            Assert.InRange(Bounds().Left - first, -1, 1);
        }
        PressTab();
        Assert.True(Until(IsOpen, 2000), "出ない");
        TogglePause(); // 出したバーで止める
        WaitClosed();
        Wait(300);
        Assert.InRange(Bounds().Left - first, -1, 1);
    }

    [DesktopFact]
    public void タブで一時停止と録画中が分かる()
    {
        Show();
        WaitClosed();
        string Mark() => On(b => (string)b.BarTab.Content);
        void Press(Func<RecordBar, System.Windows.Controls.Button> button) =>
            On(b =>
            {
                button(b).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                return true;
            });

        Assert.Equal("●", Mark());
        Press(b => b.ClipButton);
        Assert.True(Until(() => Mark() == "■"), "録画中にならない");
        Snapshot("bar-tab-clip");
        Press(b => b.PauseButton);
        Assert.Equal("❚❚", Mark());
        Wait(200);
        Snapshot("bar-tab-paused");
        Press(b => b.PauseButton);
        Assert.Equal("■", Mark());
        Wait(700);
        Press(b => b.ClipButton);
        Assert.True(Until(() => Mark() == "●"), "記録中に戻らない");
        Assert.False(IsOpen()); // ホットキーで操作しても出てこない
    }

    [E2EFact]
    public void マウスを乗せると出て_離すと引っ込み_中のボタンを押せる()
    {
        Show(realPointer: true);
        var start = Bounds();
        MoveCursor((start.Left + start.Right) / 2, start.Bottom + 300); // 前からバーの所にあっても始められるように
        WaitClosed();
        Wait(300);
        var tab = Bounds();
        var (x, y) = ((tab.Left + tab.Right) / 2, (tab.Top + tab.Bottom) / 2);

        MoveCursor(x, y);
        Assert.True(Until(IsOpen, 2000), "乗せても出ない");
        Wait(1800);
        Assert.True(IsOpen(), "乗せているのに引っ込んだ");

        var foreground = Native.GetForegroundWindow();
        ClickOn(hwnd, "PauseButton");
        Assert.True(Until(() => On(b => (string)b.PauseButton.Content) == "再開"), "ボタンを押せない");
        Assert.Equal(foreground, Native.GetForegroundWindow()); // 前面は奪わない

        MoveCursor(x, y + 300);
        WaitClosed();
    }
}
