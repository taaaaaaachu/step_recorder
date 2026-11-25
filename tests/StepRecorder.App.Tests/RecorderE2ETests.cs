using System.Diagnostics;
using System.IO;
using StepRecorder.Core;
using static StepRecorder.App.Tests.Desktop;

namespace StepRecorder.App.Tests;

/// <summary>テスト用の WinForms アプリを実際に操作して、記録エンジンが拾えるかを確かめる。</summary>
public sealed class RecorderE2ETests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "sr-e2e-" + Guid.NewGuid());
    Process? target;

    public void Dispose()
    {
        try { target?.Kill(); } catch (InvalidOperationException) { }
        // STEP_RECORDER_E2E_OUT を指定すると、目で確かめられるよう出力を HTML 付きで残す
        if (Environment.GetEnvironmentVariable("STEP_RECORDER_E2E_OUT") is { Length: > 0 } outDir && File.Exists(Path.Combine(dir, SessionStore.FileName)))
        {
            Export.SaveAll(SessionStore.Load(dir), dir, GroupMode.Sequence);
            Directory.CreateDirectory(outDir);
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(outDir, Path.GetRelativePath(dir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(file, to, true);
            }
        }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    [E2EFact]
    public void 手動撮影_クリック_入力_ダイアログを記録できる()
    {
        target = Launch(SampleExe);
        var main = target.MainWindowHandle;
        var engine = new RecorderEngine(new AppTarget(target), dir, new Settings { DefaultMode = RecordMode.Click });
        engine.Start();

        engine.CaptureManual();
        Wait(500);

        ClickOn(main, "nameBox");
        Wait(500);
        TypeText(main, "abc");
        Wait(300);
        Assert.Equal("abc", ValueOf(main, "nameBox"));

        engine.Mode = RecordMode.Manual; // OK のクリックは撮らず、出てきたダイアログを手動で撮る
        ClickOn(main, "okButton");
        Wait(800);
        engine.CaptureManual();
        Wait(800);

        var session = Stop(engine);
        Assert.Equal("SampleTarget", session.TargetName);
        Assert.Equal("SampleTarget.exe", session.Evidence.AppFile);
        Assert.NotEqual("", session.Evidence.AppVersion);
        Assert.Matches(@"^\d+×\d+$", session.Evidence.Screen);
        Assert.Equal(Monitors.ScalePercent(Native.MonitorFromWindow(main, Native.MONITOR_DEFAULTTONEAREST)), session.Evidence.ScalePercent);
        Assert.Equal(Environment.MachineName, session.Evidence.MachineName);
        Assert.Equal(3, session.Steps.Count);

        var manual = session.Steps[0];
        Assert.Equal(CaptureTrigger.Manual, manual.Trigger);
        Assert.Equal("サンプル画面", manual.ScreenName);

        var click = session.Steps[1];
        Assert.Equal(CaptureTrigger.Click, click.Trigger);
        Assert.Equal("『氏名』欄をクリック\n『氏名』欄に「abc」と入力", click.Description);
        Assert.Single(click.Highlights);

        var dialog = session.Steps[2];
        Assert.Equal("確認", dialog.ScreenName);
        // ダイアログは、メインウィンドウごと撮ったアプリ全体の画像を、ダイアログの範囲にトリミングした状態になる
        Assert.NotNull(dialog.Crop);
        Assert.True(dialog.ImageWidth >= click.ImageWidth && dialog.ImageHeight >= click.ImageHeight, "画像がアプリ全体になっていない");
        Assert.True(dialog.Crop.Width < dialog.ImageWidth && dialog.Crop.Height < dialog.ImageHeight);
        Assert.Null(click.Crop); // メインウィンドウだけのときはトリミングしない

        foreach (var s in session.Steps)
        {
            var info = new FileInfo(Path.Combine(dir, s.ImageFile));
            Assert.True(info.Exists && info.Length > 1000, $"{s.ImageFile} が撮れていない");
            Assert.True(s.ImageWidth > 100 && s.ImageHeight > 50);
        }
    }

    [E2EFact]
    public void カーソルを写すかどうかを切り替えられる()
    {
        target = Launch(SampleExe);
        var main = target.MainWindowHandle;
        var frame = Native.FrameBounds(main);
        var (x, y) = (frame.Left + 60, frame.Top + 150); // 部品の無い所
        MoveCursor(x, y);
        Wait(1000); // 起動直後のウィンドウの描画(開くアニメーション)が終わるのを待つ

        var without = WindowCapture.Capture(main, cursor: false)!;
        var with = WindowCapture.Capture(main, cursor: true)!;
        if (Environment.GetEnvironmentVariable("STEP_RECORDER_E2E_OUT") is { Length: > 0 } outDir)
            WindowCapture.SavePng(with.Image, Path.Combine(outDir, "cursor.png"));
        Assert.Equal((without.Image.PixelWidth, without.Image.PixelHeight), (with.Image.PixelWidth, with.Image.PixelHeight));

        // 違いはカーソルの位置(ホットスポットから右下へ数十ピクセル)にだけある
        var (a, b, w) = (VideoE2ETests.Bgr24(without.Image), VideoE2ETests.Bgr24(with.Image), without.Image.PixelWidth);
        var changed = Enumerable.Range(0, a.Length / 3).Where(i => a[i * 3] != b[i * 3] || a[i * 3 + 1] != b[i * 3 + 1] || a[i * 3 + 2] != b[i * 3 + 2])
            .Select(i => (X: i % w, Y: i / w)).ToList();
        Assert.NotEmpty(changed);
        var (cx, cy) = (x - with.Bounds.Left, y - with.Bounds.Top);
        Assert.All(changed, p => Assert.True(p.X >= cx - 8 && p.X <= cx + 64 && p.Y >= cy - 8 && p.Y <= cy + 64, $"カーソル以外の所が違う: {p}"));
    }

    [E2EFact]
    public void 録画ボタンで動画のステップを録れる()
    {
        target = Launch(SampleExe);
        var main = target.MainWindowHandle;
        var engine = new RecorderEngine(new AppTarget(target), dir, new Settings { DefaultMode = RecordMode.Click });
        engine.Start();
        Wait(300);

        engine.ToggleClip();
        Wait(500);
        ClickOn(main, "nameBox"); // 録画中のクリックは撮らず、動画のステップの操作になる
        Wait(1000);
        engine.ToggleClip();
        Wait(300);
        ClickOn(main, "nameBox"); // 録画を止めたら、いつもどおり撮る
        Wait(500);

        var session = Stop(engine);
        Assert.Equal([CaptureTrigger.Video, CaptureTrigger.Click], session.Steps.Select(s => s.Trigger));
        var clip = session.Steps[0];
        Assert.Equal("videos/0001.mp4", clip.VideoFile);
        Assert.Equal("『氏名』欄をクリック", clip.Description);
        Assert.InRange(clip.Operations[0].VideoTime!.Value.TotalSeconds, 0.2, 1.0); // 録り始めて 0.5 秒後のクリック(分割で振り分けるのに使う)
        Assert.Null(session.Steps[1].Operations[0].VideoTime); // 録画を止めたあとの操作には付けない
        Assert.Empty(clip.Highlights);
        var (duration, w, h) = VideoE2ETests.VideoInfo(Path.Combine(dir, clip.VideoFile!));
        Assert.InRange(duration.TotalSeconds, 1.2, 2.5);
        Assert.Equal((w, h), (clip.ImageWidth, clip.ImageHeight)); // ポスターは動画の最初のフレーム
        Assert.True(new FileInfo(Path.Combine(dir, clip.ImageFile)).Length > 1000);
    }

    [E2EFact]
    public void 録画モードでは全編を録り_ステップに動画上の時刻を付ける()
    {
        target = Launch(SampleExe);
        var main = target.MainWindowHandle;
        var engine = new RecorderEngine(new AppTarget(target), dir, new Settings { DefaultMode = RecordMode.Video });
        engine.Start();
        Wait(800);
        ClickOn(main, "nameBox");
        Wait(1000);
        ClickOn(main, "okButton");
        Wait(1000);
        engine.ToggleClip(); // 録画モードでは使えない
        Wait(300);

        var stages = new List<string>();
        var session = Stop(engine, stages);
        Assert.Equal(["撮った画像を保存しています(残り 0 件)", "動画を書き終えています"], stages); // 停止中に出す進み具合
        Assert.Equal("video.mp4", session.VideoFile);
        Assert.All(session.Steps, s => Assert.Null(s.VideoFile));
        var times = session.Steps.Select(s => s.VideoTime!.Value.TotalSeconds).ToList();
        Assert.Equal(2, times.Count);
        Assert.InRange(times[0], 0.5, 1.5);
        Assert.InRange(times[1] - times[0], 0.7, 1.6);
        Assert.InRange(VideoE2ETests.VideoInfo(Path.Combine(dir, "video.mp4")).Duration.TotalSeconds, 2.5, 4.5);
    }

    [DesktopFact]
    public void 全編を録れなければ_壊れた動画を記録に残さない()
    {
        target = Open(SampleExe);
        Directory.CreateDirectory(Path.Combine(dir, "video.mp4")); // 同名のフォルダがあるので書けない
        var engine = new RecorderEngine(new AppTarget(target), dir, new Settings { DefaultMode = RecordMode.Video });
        engine.Start();
        Wait(500);

        var session = engine.Stop();
        Assert.Null(session.VideoFile);
        Assert.True(File.Exists(Path.Combine(dir, "error.log")));
    }

    [DesktopFact]
    public void 一時停止すると全編の動画も止まる()
    {
        target = Open(SampleExe);
        var engine = new RecorderEngine(new AppTarget(target), dir, new Settings { DefaultMode = RecordMode.Video });
        engine.Start();
        Wait(1200);
        engine.Paused = true;
        Wait(2000);
        engine.Paused = false;
        Wait(800);

        var session = Stop(engine);
        Assert.InRange(VideoE2ETests.VideoInfo(Path.Combine(dir, session.VideoFile!)).Duration.TotalSeconds, 1.2, 2.4);
    }

    [DesktopFact]
    public void 始めてすぐ止めても失敗にしない()
    {
        target = Open(SampleExe);
        var engine = new RecorderEngine(new AppTarget(target), dir, new Settings { DefaultMode = RecordMode.Video });
        engine.Start();
        engine.Paused = true; // 停止ボタンは、まず録るのをやめてから止める

        Stop(engine); // error.log が無いこと
    }

    [E2EFact]
    public void 録画中に押したキーを動画上の時刻と一緒に残す()
    {
        target = Launch(SampleExe);
        var main = target.MainWindowHandle;
        var engine = new RecorderEngine(new AppTarget(target), dir, new Settings { DefaultMode = RecordMode.Video });
        engine.Start();
        Wait(800);
        ClickOn(main, "nameBox");
        Wait(500);
        PressKeys(main, 0x41); // a
        PressKeys(main, 0x10, 0x42); // Shift+B
        TypeText(main, "c"); // 文字だけを送り込む打鍵(VK_PACKET)
        Wait(300);
        PressKeys(main, 0x11, 0x41); // Ctrl+A
        PressKeys(main, 0x25); // ←
        Wait(500);

        var session = Stop(engine);
        var keys = session.Keys!;
        Assert.Equal(["a", "B", "c", "Ctrl+A", "←"], keys.Select(k => k.Text));
        Assert.Equal([true, true, true, false, false], keys.Select(k => k.IsChar));
        Assert.All(keys.Zip(keys.Skip(1)), p => Assert.True(p.First.Time <= p.Second.Time));
        Assert.InRange(keys[0].Time.TotalSeconds, 1.0, 2.2); // 動画上の時刻(録り始めから)
        Assert.Equal(["aBc", "Ctrl+A", "←"], KeyOverlay.Chips(KeyOverlay.StrokesFor(session, new Step { VideoFile = "video.mp4" })).Select(c => c.Text));
    }

    [E2EFact]
    public void 録画中のクリックとホイールを動画上の時刻と位置と一緒に残す()
    {
        target = Launch(SampleExe);
        var main = target.MainWindowHandle;
        var engine = new RecorderEngine(new AppTarget(target), dir, new Settings { DefaultMode = RecordMode.Video });
        engine.Start();
        Wait(800);
        var frame = Native.FrameBounds(main);
        var box = Find(main, "nameBox").Current.BoundingRectangle;
        var (x, y) = ((int)(box.X + box.Width / 2), (int)(box.Y + box.Height / 2));
        Click(main, x, y);
        Wait(300);
        Wheel(main, x, y, -2);
        Wait(300);
        SideButton(main, x, y, back: true);
        Wait(500);

        var session = Stop(engine);
        var mouse = session.Mouse!;
        Assert.Equal([MouseAction.Left, MouseAction.WheelDown, MouseAction.Back], mouse.Select(m => m.Action));
        Assert.Contains("マウスの戻るボタン を押す", session.Steps[^1].Description); // 説明文にも残す
        Assert.All(mouse, m => Assert.Equal((x - frame.Left, y - frame.Top), (m.X, m.Y))); // 動画上の位置(ウィンドウの左上から)
        Assert.InRange(mouse[0].Time.TotalSeconds, 0.5, 1.8);
        Assert.True(mouse[1].Time > mouse[0].Time);
        Assert.Equal(3, MouseOverlay.Marks(MouseOverlay.StrokesFor(session, new Step { VideoFile = "video.mp4" })).Count);
    }

    [E2EFact]
    public void 録画中に押したまま動かした位置を_離すまで残す()
    {
        target = Launch(SampleExe);
        var main = target.MainWindowHandle;
        var engine = new RecorderEngine(new AppTarget(target), dir, new Settings { DefaultMode = RecordMode.Video });
        engine.Start();
        Wait(800);
        var frame = Native.FrameBounds(main);
        var box = Find(main, "nameBox").Current.BoundingRectangle;
        var (x, y) = ((int)box.X + 5, (int)(box.Y + box.Height / 2));
        Drag(main, x, y, x + 100, y);
        Wait(300);
        Click(main, x, y); // ただのクリックはドラッグにしない
        Wait(500);

        var mouse = Stop(engine).Mouse!;
        Assert.Equal(2, mouse.Count);
        var drag = mouse[0].Drag!;
        Assert.True(drag.Count >= 2, $"動かした位置が {drag.Count} 個");
        Assert.Equal((x + 100 - frame.Left, y - frame.Top), (drag[^1].X, drag[^1].Y)); // 最後は離した所
        Assert.True(drag[0].Time > mouse[0].Time && drag[^1].Time - mouse[0].Time < TimeSpan.FromSeconds(1));
        Assert.Null(mouse[1].Drag);
    }

    [E2EFact]
    public void 録画ボタンの動画で押したキーはそのステップに残す()
    {
        target = Launch(SampleExe);
        var main = target.MainWindowHandle;
        var engine = new RecorderEngine(new AppTarget(target), dir, new Settings { DefaultMode = RecordMode.Click });
        engine.Start();
        ClickOn(main, "nameBox"); // 先にクリックのステップを作っておく(キーはこちらに入らないこと)
        Wait(500);
        engine.ToggleClip();
        Wait(800);
        PressKeys(main, 0x0D); // Enter
        Wait(500);
        engine.ToggleClip();
        Wait(300);

        var session = Stop(engine);
        Assert.Null(session.Keys);
        Assert.Null(session.Steps[0].Keys);
        var key = Assert.Single(session.Steps[1].Keys!);
        Assert.Equal("Enter", key.Text);
        Assert.Null(session.Mouse); // 動画を録っていない間のクリックは残さない
        Assert.InRange(key.Time.TotalSeconds, 0.5, 1.6); // 録画ボタンの動画の時刻
    }

    /// <summary>止めてセッションを保存する。記録中の例外(error.log)があれば失敗させる。</summary>
    Session Stop(RecorderEngine engine, List<string>? stages = null)
    {
        var session = engine.Stop(text => { lock (dir) stages?.Add(text); });
        var log = Path.Combine(dir, "error.log");
        Assert.False(File.Exists(log), File.Exists(log) ? File.ReadAllText(log) : "");
        SessionStore.Save(session, dir);
        return session;
    }
}
