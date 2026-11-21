using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StepRecorder.Core;
using static StepRecorder.App.Tests.Desktop;

namespace StepRecorder.App.Tests;

public sealed class EditorSmokeTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "sr-editor-" + Guid.NewGuid());
    Process? app;

    public void Dispose()
    {
        try { app?.Kill(); app?.WaitForExit(5000); } catch (InvalidOperationException) { }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    /// <summary>記録フォルダを渡して起動すると、開始画面を出さずに編集画面が開く。</summary>
    [E2EFact]
    public void 記録フォルダを渡すと編集画面が開く()
    {
        var rec = new Recording(new Session { ProcessName = "Demo", CreatedAt = DateTimeOffset.Now });
        for (var i = 0; i < 2; i++)
        {
            var file = rec.NextImageFile;
            WindowCapture.SavePng(Solid(400, 300, i == 0 ? Colors.SteelBlue : Colors.SeaGreen), Path.Combine(dir, file));
            var st = rec.AddStep(CaptureTrigger.Manual, new($"画面{i + 1}", "W", "Demo"), file, 400, 300, DateTimeOffset.Now);
            st.Description = $"説明{i + 1}";
            st.Highlights.Add(new(50, 50, 120, 60));
        }
        rec.Session.Steps[0].Crop = new(20, 20, 300, 200);
        SessionStore.Save(rec.Session, dir);

        app = Launch(Path.Combine(AppContext.BaseDirectory, "StepRecorder.exe"), $"\"{dir}\"");
        var editor = app.MainWindowHandle;
        Assert.StartsWith("step_recorder 編集", Native.Text(editor));
        Wait(500); // 描画を待つ

        var shot = WindowCapture.Capture(editor);
        Assert.NotNull(shot);
        Assert.True(shot.Image.PixelWidth > 600);
        if (Environment.GetEnvironmentVariable("STEP_RECORDER_E2E_OUT") is { Length: > 0 } outDir)
            WindowCapture.SavePng(shot.Image, Path.Combine(outDir, "editor.png"));

        // ステップを削除して、Ctrl+Z で元に戻す
        int Count() => Find(editor, "Steps").FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Count;
        Assert.Equal(2, Count());
        ClickOn(editor, new PropertyCondition(AutomationElement.NameProperty, "削除"));
        Wait(300);
        Assert.Equal(1, Count());
        PressKeys(editor, 0x11, 0x5A); // Ctrl+Z
        Wait(300);
        Assert.Equal(2, Count());
    }

    [E2EFact]
    public void 編集画面はズームできる()
    {
        var rec = new Recording(new Session { ProcessName = "Demo", CreatedAt = DateTimeOffset.Now });
        var file = rec.NextImageFile;
        WindowCapture.SavePng(Solid(3000, 2000, Colors.SteelBlue), Path.Combine(dir, file)); // 表示欄より大きい画像
        rec.AddStep(CaptureTrigger.Manual, new("大きい画面", "W", "Demo"), file, 3000, 2000, DateTimeOffset.Now);
        SessionStore.Save(rec.Session, dir);

        app = Launch(Path.Combine(AppContext.BaseDirectory, "StepRecorder.exe"), $"\"{dir}\"");
        var editor = app.MainWindowHandle;
        int Percent() => int.Parse(Find(editor, "ZoomLabel").Current.Name.TrimEnd('%'));
        Wait(500);

        var fitted = Percent();
        Assert.InRange(fitted, 10, 99); // 最初は全体が収まる大きさ

        ClickOn(editor, new PropertyCondition(AutomationElement.NameProperty, "+"));
        Wait(300);
        Assert.InRange(Percent(), fitted * 1.25 - 1, fitted * 1.25 + 1);

        var r = Find(editor, "Scroller").Current.BoundingRectangle;
        var before = Percent();
        CtrlWheel(editor, (int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2), 1);
        Wait(300);
        Assert.True(Percent() > before, $"Ctrl+ホイールで拡大されない: {before}% → {Percent()}%");

        ClickOn(editor, new PropertyCondition(AutomationElement.NameProperty, "100%"));
        Wait(300);
        Assert.Equal(100, Percent());

        // ホイールボタンを押したまま左上へドラッグすると、画像が左上へ動く(スクロール位置は右下へ)
        var scroll = (ScrollPattern)Find(editor, "Scroller").GetCurrentPattern(ScrollPattern.Pattern);
        var (h0, v0) = (scroll.Current.HorizontalScrollPercent, scroll.Current.VerticalScrollPercent);
        var (cx, cy) = ((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
        MiddleDrag(editor, cx, cy, cx - 200, cy - 150);
        Wait(300);
        Assert.True(scroll.Current.HorizontalScrollPercent > h0 && scroll.Current.VerticalScrollPercent > v0,
            $"ドラッグで動かない: ({h0:0.#}, {v0:0.#}) → ({scroll.Current.HorizontalScrollPercent:0.#}, {scroll.Current.VerticalScrollPercent:0.#})");
    }


    /// <summary>キーボードの LANG1/LANG2(VK_IME_ON/OFF・VK_KANA/HANJA)で、説明欄の IME がオン・オフになる。</summary>
    [E2EFact]
    public void 説明欄でLANG1とLANG2がIMEを切り替える()
    {
        var rec = new Recording(new Session { ProcessName = "Demo", CreatedAt = DateTimeOffset.Now });
        var file = rec.NextImageFile;
        WindowCapture.SavePng(Solid(400, 300, Colors.SteelBlue), Path.Combine(dir, file));
        rec.AddStep(CaptureTrigger.Manual, new("画面", "W", "Demo"), file, 400, 300, DateTimeOffset.Now);
        SessionStore.Save(rec.Session, dir);

        app = Launch(Path.Combine(AppContext.BaseDirectory, "StepRecorder.exe"), $"\"{dir}\"");
        var editor = app.MainWindowHandle;
        Wait(500);
        ClickOn(editor, "Description");
        Wait(300);

        foreach (var (on, off) in new (ushort, ushort)[] { (0x16, 0x1A), (0x15, 0x19) })
        {
            PressKeys(editor, on);
            Wait(300);
            Assert.True(Native.ImeOpen(editor), $"0x{on:X2} で IME がオンにならない");
            PressKeys(editor, off);
            Wait(300);
            Assert.False(Native.ImeOpen(editor), $"0x{off:X2} で IME がオフにならない");
        }
    }



    /// <summary>表示欄だけを別ウインドウに出し、閉じると編集画面に戻る。</summary>
    [E2EFact]
    public void 表示欄を別ウインドウに出して戻せる()
    {
        var rec = new Recording(new Session { ProcessName = "Demo", CreatedAt = DateTimeOffset.Now });
        WindowCapture.SavePng(Solid(400, 300, Colors.SteelBlue), Path.Combine(dir, rec.NextImageFile));
        rec.AddStep(CaptureTrigger.Manual, new("画面", "W", "Demo"), rec.NextImageFile, 400, 300, DateTimeOffset.Now);
        SessionStore.Save(rec.Session, dir);
        app = Launch(Path.Combine(AppContext.BaseDirectory, "StepRecorder.exe"), $"\"{dir}\"");
        var editor = app.MainWindowHandle;
        Wait(500);

        // WPF の子ウインドウ(Owner 付き)は UI Automation では編集画面の下に出るので、表示欄がどちらのウインドウにあるかは親をたどって見る
        string? WindowOfScroller()
        {
            var e = AutomationElement.FromHandle(editor).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "Scroller"));
            for (; e != null; e = TreeWalker.ControlViewWalker.GetParent(e))
                if (e.Current.ControlType == ControlType.Window) return e.Current.Name;
            return null;
        }
        var popupTitle = $"step_recorder プレビュー — {Path.GetFileName(dir)}";

        Assert.StartsWith("step_recorder 編集", WindowOfScroller());
        ClickOn(editor, new PropertyCondition(AutomationElement.NameProperty, "別ウインドウで表示"));
        Assert.True(Until(() => WindowOfScroller() == popupTitle), "表示欄が別ウインドウに出ない");

        // 別ウインドウが編集画面の上に重なっていることがあるので、マウスではなく UI Automation で押す
        ((InvokePattern)Find(editor, new PropertyCondition(AutomationElement.NameProperty, "表示欄をここに戻す")).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        Assert.True(Until(() => WindowOfScroller()?.StartsWith("step_recorder 編集") == true), "表示欄が戻らない");
    }

    /// <summary>画面一覧モードにすると、一覧も出力と同じく画面ごとに区切られる。</summary>
    [E2EFact]
    public void 画面一覧モードでは一覧を画面ごとに区切る()
    {
        var rec = new Recording(new Session { ProcessName = "Demo", CreatedAt = DateTimeOffset.Now });
        foreach (var name in new[] { "画面A", "画面B", "画面A" })
        {
            var file = rec.NextImageFile;
            WindowCapture.SavePng(Solid(200, 100, Colors.SteelBlue), Path.Combine(dir, file));
            rec.AddStep(CaptureTrigger.Manual, new(name, "W", "Demo"), file, 200, 100, DateTimeOffset.Now);
        }
        SessionStore.Save(rec.Session, dir);
        app = Launch(Path.Combine(AppContext.BaseDirectory, "StepRecorder.exe"), $"\"{dir}\"");
        var editor = app.MainWindowHandle;
        Wait(500);

        List<string> Headers() => Find(editor, "Steps").FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>().Select(e => e.Current.Name).Where(n => n.StartsWith("画面") || n.Length > 0 && char.IsDigit(n[0])).ToList();
        void Choose(string item)
        {
            var group = Find(editor, "Group");
            ((ExpandCollapsePattern)group.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
            Wait(300);
            ((SelectionItemPattern)group.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, item))
                .GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            ((ExpandCollapsePattern)group.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse();
            Wait(500);
        }

        int Groups() => Find(editor, "Steps").FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group)).Count;
        Choose("手順モード(撮った順)");
        Assert.Equal(["1. 画面A", "2. 画面B", "3. 画面A"], Headers());
        Assert.Equal(0, Groups());
        Choose("画面一覧モード(画面ごと)");
        Assert.Equal(["1. 画面A", "3. 画面A", "2. 画面B"], Headers()); // 出力と同じく、同じ画面がまとまる
        Assert.Equal(2, Groups());
        if (Environment.GetEnvironmentVariable("STEP_RECORDER_E2E_OUT") is { Length: > 0 } shotDir)
            WindowCapture.SavePng(WindowCapture.Capture(editor)!.Image, Path.Combine(shotDir, "editor-byscreen.png"));
    }

    /// <summary>録画モードの記録を開くと、全編の動画からフレームと区間をステップに足せる。配布用にも書き出せる。</summary>
    [E2EFact]
    public void 録画モードの記録では全編からフレームと区間を足せる()
    {
        // 録画モードの記録を作る(サンプルアプリを3秒録画し、0.5 秒の時点のステップを1つ持つ)
        var sample = Launch(SampleExe);
        try
        {
            var rec = VideoRecorder.Start(sample.MainWindowHandle, Path.Combine(dir, "video.mp4"), cursor: false);
            Wait(3000);
            rec.Stop();
        }
        finally { sample.Kill(); }
        var poster = VideoClips.Frame(Path.Combine(dir, "video.mp4"), TimeSpan.FromSeconds(0.5));
        var recording = new Recording(new Session { ProcessName = "SampleTarget", CreatedAt = DateTimeOffset.Now, VideoFile = "video.mp4" });
        WindowCapture.SavePng(poster, Path.Combine(dir, recording.NextImageFile));
        var first = recording.AddStep(CaptureTrigger.Click, new("サンプル画面", "W", "SampleTarget"), recording.NextImageFile, poster.PixelWidth, poster.PixelHeight, DateTimeOffset.Now);
        first.VideoTime = TimeSpan.FromSeconds(0.5);
        SessionStore.Save(recording.Session, dir);

        app = Launch(Path.Combine(AppContext.BaseDirectory, "StepRecorder.exe"), $"\"{dir}\"");
        var editor = app.MainWindowHandle;
        Wait(1000);
        var slider = (RangeValuePattern)Find(editor, "TimeSlider").GetCurrentPattern(RangeValuePattern.Pattern);
        Until(() => slider.Current.Maximum > 1); // 動画を開き終えると長さが分かる
        Assert.InRange(slider.Current.Maximum, 2.5, 3.5); // 全編の長さ
        void At(double sec) { slider.SetValue(sec); Wait(300); }
        void Press(string name) { ClickOn(editor, new PropertyCondition(AutomationElement.NameProperty, name)); Wait(800); }

        At(2.0);
        if (Environment.GetEnvironmentVariable("STEP_RECORDER_E2E_OUT") is { Length: > 0 } seekDir) // シークすると、メインの表示欄に全編の動画が出る
        {
            Wait(500);
            WindowCapture.SavePng(WindowCapture.Capture(editor)!.Image, Path.Combine(seekDir, "editor-seek.png"));
        }
        Press("このフレームをステップに追加");
        At(1.0);
        Press("区間の開始");
        At(1.8);
        Press("区間の終了");
        Press("区間を動画ステップに追加");
        // 足した動画のステップが選ばれているので、再生できる(区間は 0.8 秒なので、押した直後から確かめる)
        ClickOn(editor, new PropertyCondition(AutomationElement.NameProperty, "▶ 再生"));
        Assert.True(Until(() => AutomationElement.FromHandle(editor).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "■ 停止")) != null), "再生が始まらない");
        if (Environment.GetEnvironmentVariable("STEP_RECORDER_E2E_OUT") is { Length: > 0 } shotDir)
        {
            Wait(500);
            WindowCapture.SavePng(WindowCapture.Capture(editor)!.Image, Path.Combine(shotDir, "editor-video.png"));
        }
        PressKeys(editor, 0x11, 0x53); // Ctrl+S
        Wait(800);

        var saved = SessionStore.Load(dir);
        Assert.Equal([0.5, 1.0, 2.0], saved.Steps.Select(s => Math.Round(s.VideoTime!.Value.TotalSeconds, 1))); // 時刻の順に並ぶ
        var clip = saved.Steps[1];
        Assert.Equal(("video.mp4", CaptureTrigger.Video), (clip.VideoFile, clip.Trigger));
        Assert.Equal((1.0, 1.8), (Math.Round(clip.VideoStart!.Value.TotalSeconds, 1), Math.Round(clip.VideoEnd!.Value.TotalSeconds, 1)));
        Assert.All(saved.Steps, s => Assert.True(File.Exists(Path.Combine(dir, s.ImageFile))));

        // 配布用の書き出し(mp4 と GIF)。トリミングも反映される
        clip.Crop = new(0, 0, 200, 100);
        foreach (var format in new[] { ClipFormat.Mp4, ClipFormat.Gif })
        {
            var outDir = Path.Combine(dir, "dist-" + format);
            var reported = new List<double>();
            Export.ExportForDistribution(saved, dir, outDir, GroupMode.Sequence, format, (st, c, p) => VideoClips.ExportClip(dir, st, format, c, p, reported.Add));
            Assert.NotEmpty(reported); // 進み具合(0〜1)が増えながら届く
            Assert.All(reported, v => Assert.InRange(v, 0, 1));
            Assert.Equal(reported.Order(), reported);
            Assert.False(File.Exists(Path.Combine(outDir, SessionStore.FileName)));
            Assert.False(File.Exists(Path.Combine(outDir, "video.mp4")));
            if (format == ClipFormat.Mp4)
            {
                var (duration, w, h) = VideoE2ETests.VideoInfo(Path.Combine(outDir, "clips", "0003.mp4"));
                Assert.InRange(duration.TotalSeconds, 0.6, 1.0);
                Assert.Equal((200, 100), (w, h));
                Assert.True(File.Exists(Path.Combine(outDir, "clips", "0003.png")));
            }
            else
            {
                var gif = new GifBitmapDecoder(new Uri(Path.Combine(outDir, "clips", "0003.gif")), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Assert.Equal(8, gif.Frames.Count); // 10fps で 0.8 秒
                Assert.Equal(200, gif.Frames[0].PixelWidth);
            }
        }
    }

    static BitmapSource Solid(int w, int h, Color c)
    {
        var px = new byte[w * h * 3];
        for (var i = 0; i < px.Length; i += 3) (px[i], px[i + 1], px[i + 2]) = (c.B, c.G, c.R);
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, px, w * 3);
        b.Freeze();
        return b;
    }
}
