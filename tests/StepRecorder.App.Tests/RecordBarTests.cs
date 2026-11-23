using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    void Show(RecordMode mode = RecordMode.Click, bool realPointer = false, bool animated = true)
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
            bar.Animated = animated; // 走らせる PC の「アニメーション効果」の設定に左右されないように
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

    /// <summary>見えている方(バーかタブ)のスクリーン上の範囲。滑り終えてから呼ぶ。</summary>
    Native.RECT Bounds() => On(b =>
    {
        FrameworkElement part = b.IsBarOpen ? b.Panel : b.BarTab;
        var (from, to) = (part.PointToScreen(new Point()), part.PointToScreen(new Point(part.ActualWidth, part.ActualHeight)));
        return new Native.RECT { Left = (int)Math.Round(from.X), Top = (int)Math.Round(from.Y), Right = (int)Math.Round(to.X), Bottom = (int)Math.Round(to.Y) };
    });

    bool IsOpen() => On(b => b.IsBarOpen);

    void WaitClosed()
    {
        Assert.True(Until(() => !IsOpen(), 5000), "引っ込まない(マウスがバーの上にある?)");
        Wait(450); // 滑り終えるのを待つ
    }

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
        Wait(450);
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
    public void ピンで留めると引っ込まず_外すと引っ込む()
    {
        Show();
        Snapshot("pin-off");
        var pin = (TogglePattern)Find(hwnd, "PinButton").GetCurrentPattern(TogglePattern.Pattern);
        string Icon() => On(b => ((System.Windows.Controls.TextBlock)b.PinButton.Template.FindName("Icon", b.PinButton)).Text);
        Assert.Equal("\uE718", Icon());
        pin.Toggle(); // 読み上げなどからの押し方(Click は上がらない)でも効くこと
        Assert.Equal(ToggleState.On, pin.Current.ToggleState); // 読み上げでも状態が分かる
        Wait(4500); // マウスが無ければ、とうに引っ込んでいる時間
        Assert.True(IsOpen(), "留めたのに引っ込んだ");
        double Angle() => On(b => (((System.Windows.Controls.TextBlock)b.PinButton.Template.FindName("Icon", b.PinButton)).RenderTransform as RotateTransform)?.Angle ?? 0);
        Assert.Equal(("\uE840", -45d), (Icon(), Angle())); // 針が真下
        Snapshot("pin-on");

        pin.Toggle();
        Assert.Equal(("\uE718", 0d), (Icon(), Angle()));
        WaitClosed();
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

    List<double> Track(Func<RecordBar, double> value, int ms)
    {
        var values = new List<double>();
        for (var clock = Stopwatch.StartNew(); clock.ElapsedMilliseconds < ms; Thread.Sleep(10)) values.Add(On(value));
        return values;
    }

    [DesktopFact]
    public void 滑って引っ込み_滑って出る()
    {
        Show();
        var height = On(b => b.Panel.ActualHeight);
        Assert.True(Until(() => !IsOpen(), 5000), "引っ込まない");
        var closing = Track(b => b.PanelSlide.Y, 450);
        Assert.True(closing.Distinct().Count(y => y < -0.5 && y > -height + 0.5) >= 3, "引っ込む途中の位置が無い");
        Assert.Equal(-height, closing[^1], 1);
        Assert.Equal(Visibility.Hidden, On(b => b.Panel.Visibility));
        Assert.Equal(0, On(b => b.TabSlide.Y), 1);

        PressTab();
        var opening = Track(b => b.PanelSlide.Y, 450);
        Assert.True(opening.Distinct().Count(y => y < -0.5 && y > -height + 0.5) >= 3, "出る途中の位置が無い");
        Assert.Equal(0, opening[^1], 1);
        Assert.Equal(Visibility.Hidden, On(b => b.BarTab.Visibility));
    }

    [DesktopFact]
    public void 引っ込む途中で開き直すと_そこから戻る()
    {
        Show();
        Assert.True(Until(() => !IsOpen(), 5000), "引っ込まない");
        On(b =>
        {
            b.BarTab.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            return true;
        });
        Wait(500);
        Assert.Equal(Visibility.Visible, On(b => b.Panel.Visibility)); // 先に始めた「消す」に負けない
        Assert.True(On(b => b.Panel.IsHitTestVisible));
        Assert.Equal(0, On(b => b.PanelSlide.Y), 1);
    }

    [DesktopFact]
    public void アニメーション効果が切ってあれば_すぐに入れ替わる()
    {
        Show(animated: false);
        Assert.True(Until(() => !IsOpen(), 5000), "引っ込まない");
        Assert.Equal((Visibility.Hidden, 0d), On(b => (b.Panel.Visibility, b.TabSlide.Y)));
        Assert.True(On(b =>
        {
            b.BarTab.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            return b.Panel.IsVisible && b.PanelSlide.Y == 0 && !b.BarTab.IsVisible;
        }));
    }

    [DesktopFact]
    public void いちばん長い文言でも固定幅に収まる()
    {
        Show();
        var (panel, window) = On(b =>
        {
            b.State.Text = "● 記録中";
            (b.ClipButton.Content, b.ClipButton.Tag) = ("録画停止 100:00:05", "\uE71A");
            b.Count.Text = "99999 枚"; // ピンの分も含めて測る
            b.UpdateLayout();
            b.Panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return (b.Panel.DesiredSize.Width - b.Panel.Margin.Left, b.ActualWidth);
        });
        // 開いている間は左端を据え置くので、短い文言で中央に置いたあとに伸びても収まること
        Assert.True(On(b => b.Panel.Margin.Left) + panel <= window, $"{panel} が {window} に収まらない");
    }

    [DesktopFact]
    public void 透明な所のクリックは下へ通る()
    {
        Show();
        nint At(int x, int y) => Native.GetAncestor(Native.WindowFromPoint(new Native.POINT { X = x, Y = y }), Native.GA_ROOT);

        var open = Bounds();
        var middle = (open.Top + open.Bottom) / 2;
        Assert.Equal(hwnd, At(open.Left + 8, middle));
        Assert.NotEqual(hwnd, At(open.Left - 8, middle)); // バーの左右
        Assert.NotEqual(hwnd, At(open.Right + 8, middle));

        WaitClosed();
        var tab = Bounds();
        Assert.Equal(hwnd, At((tab.Left + tab.Right) / 2, (tab.Top + tab.Bottom) / 2));
        Assert.NotEqual(hwnd, At(open.Left + 8, middle)); // 引っ込んだあとのバーの跡
        Assert.NotEqual(hwnd, At((tab.Left + tab.Right) / 2, tab.Bottom + 4)); // タブのすぐ下
    }

    [DesktopFact]
    public void 滑っている途中も録画に写らない()
    {
        var m = Monitors.All().Single(x => x.Primary);
        using var cover = ColorWindow.Cover(m, Colors.Blue);
        ColorWindow.EnsureCovered(m);
        Show();
        WaitClosed();
        ColorWindow.EnsureCovered(m);
        var bounds = Native.ToRect(m.Bounds);
        var window = On(b => (b.Left, b.Top, b.ActualWidth, b.ActualHeight,
            Scale: PresentationSource.FromVisual(b)!.CompositionTarget.TransformToDevice.M11));
        var area = new Int32Rect((int)(window.Left * window.Scale) - m.Bounds.X, (int)(window.Top * window.Scale) - m.Bounds.Y,
            (int)(window.ActualWidth * window.Scale), (int)(window.ActualHeight * window.Scale));
        BitmapSource Strip() => new CroppedBitmap(WindowCapture.CaptureMonitor(bounds, 0).Image, area);
        static int NotBlue(BitmapSource image)
        {
            var px = VideoE2ETests.Bgr24(image);
            return Enumerable.Range(0, px.Length / 3).Count(i => px[i * 3] < 235 || px[i * 3 + 1] > 20 || px[i * 3 + 2] > 20);
        }

        // 除外する前は見えている。開く途中をコマ送りで残す(目で確かめる用)
        PressTab();
        var shots = new List<BitmapSource>();
        for (var i = 0; i < 12; i++) { shots.Add(Strip()); Wait(20); }
        Assert.Contains(shots, s => NotBlue(s) > 100);
        if (Environment.GetEnvironmentVariable("STEP_RECORDER_E2E_OUT") is { Length: > 0 } outDir)
            for (var i = 0; i < shots.Count; i++) WindowCapture.SavePng(shots[i], Path.Combine(outDir, $"slide-{i:D2}.png"));

        On(b => { Native.ExcludeFromCapture(hwnd); return true; });
        Wait(300);
        var path = Path.Combine(dir, "slide.mp4");
        var recorder = VideoRecorder.StartMonitor(m.Handle, bounds, path, cursor: false);
        WaitClosed();
        Assert.Equal(0, NotBlue(Strip()));
        PressTab();
        Wait(500);
        recorder.Stop();
        using var reader = VideoClips.Reader.Open(path);
        for (var t = TimeSpan.Zero; t < reader.Duration; t += TimeSpan.FromSeconds(1.0 / VideoRecorder.Fps))
            Assert.Equal(0, NotBlue(new CroppedBitmap(reader.Frame(t), area)));
    }

    [DesktopFact]
    public void モニターを録るときはツールチップを出さない()
    {
        Exception? error = null;
        var sta = new Thread(() =>
        {
            try
            {
                var settings = new Settings();
                // 表示しないので、フックもホットキーも録画も始まらない
                var hidden = new RecordBar(new RecorderEngine(new MonitorTarget(Monitors.All()[0]), dir, settings), settings, dir);
                Assert.Null(hidden.PinButton.ToolTip);
                Assert.Null(hidden.ClipButton.ToolTip);
            }
            catch (Exception e) { error = e; }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();
        Assert.Null(error);
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
        Wait(450);
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
