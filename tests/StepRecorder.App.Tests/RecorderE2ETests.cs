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
    public void 手動撮影_クリック_入力_画面切り替わりを記録できる()
    {
        target = Launch(SampleExe);
        var main = target.MainWindowHandle;
        var engine = new RecorderEngine(target, dir, new Settings { DefaultMode = CaptureTrigger.Click, DrawWaitMs = 300 });
        engine.Start();

        engine.CaptureManual();
        Wait(500);

        ClickOn(main, "nameBox");
        Wait(500);
        TypeText(main, "abc");
        Wait(300);
        Assert.Equal("abc", ValueOf(main, "nameBox"));

        engine.Mode = CaptureTrigger.WindowChange; // OK のクリックは撮らず、出てきたダイアログを撮る
        ClickOn(main, "okButton");
        Wait(1500);

        var session = Stop(engine);
        Assert.Equal("SampleTarget", session.ProcessName);
        Assert.Equal("SampleTarget.exe", session.Evidence.AppFile);
        Assert.NotEqual("", session.Evidence.AppVersion);
        Assert.Matches(@"^\d+×\d+$", session.Evidence.Screen);
        Assert.True(session.Evidence.ScalePercent >= 100);
        Assert.Equal(Environment.MachineName, session.Evidence.MachineName);
        Assert.True(session.Steps.Count >= 3, $"ステップ数 {session.Steps.Count}");

        var manual = session.Steps[0];
        Assert.Equal(CaptureTrigger.Manual, manual.Trigger);
        Assert.Equal("サンプル画面", manual.ScreenName);

        var click = session.Steps[1];
        Assert.Equal(CaptureTrigger.Click, click.Trigger);
        Assert.Equal("『氏名』欄をクリック\n『氏名』欄に「abc」と入力", click.Description);
        Assert.Single(click.Highlights);

        var dialog = session.Steps[2];
        Assert.Equal(CaptureTrigger.WindowChange, dialog.Trigger);
        Assert.Equal("『確認』画面が表示された", dialog.Description);
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
    public void タブの切り替えとウィンドウ内の表示の切り替えを記録できる()
    {
        target = Launch(SampleExe);
        var main = target.MainWindowHandle;
        var engine = new RecorderEngine(target, dir, new Settings { DefaultMode = CaptureTrigger.WindowChange, DrawWaitMs = 300 });
        engine.Start();
        Wait(500);

        // .NET の UI Automation が返すタブの位置はずれていることがあるので、Ctrl+Tab で次のタブへ切り替える
        ClickOn(main, "nameBox");
        Wait(300);
        PressKeys(main, 0x11, 0x09); // Ctrl+Tab
        Wait(1500);
        ClickOn(main, "toggleButton");
        Wait(1500);

        var session = Stop(engine);
        Assert.Equal(["『詳細』タブに切り替わった", "画面の表示が切り替わった"], session.Steps.Select(s => s.Description));
        Assert.Equal("サンプル画面 - 詳細", session.Steps[0].ScreenName);
        Assert.Equal("サンプル画面", session.Steps[1].ScreenName);
    }

    [E2EFact]
    public void カーソルを写すかどうかを切り替えられる()
    {
        target = Launch(SampleExe);
        var main = target.MainWindowHandle;
        var frame = Native.FrameBounds(main);
        var (x, y) = (frame.Left + 60, frame.Top + 150); // 部品の無い所
        MoveCursor(x, y);
        Wait(300);

        var without = WindowCapture.Capture(main, cursor: false)!;
        var with = WindowCapture.Capture(main, cursor: true)!;
        if (Environment.GetEnvironmentVariable("STEP_RECORDER_E2E_OUT") is { Length: > 0 } outDir)
            WindowCapture.SavePng(with.Image, Path.Combine(outDir, "cursor.png"));
        Assert.Equal((without.Image.PixelWidth, without.Image.PixelHeight), (with.Image.PixelWidth, with.Image.PixelHeight));

        // 違いはカーソルの位置(ホットスポットから右下へ数十ピクセル)にだけある
        var (a, b, w) = (without.Pixels, with.Pixels, without.Image.PixelWidth);
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
        var engine = new RecorderEngine(target, dir, new Settings { DefaultMode = CaptureTrigger.Click, DrawWaitMs = 300 });
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
        Assert.Empty(clip.Highlights);
        var (duration, w, h) = VideoE2ETests.VideoInfo(Path.Combine(dir, clip.VideoFile));
        Assert.InRange(duration.TotalSeconds, 1.2, 2.5);
        Assert.Equal((w, h), (clip.ImageWidth, clip.ImageHeight)); // ポスターは動画の最初のフレーム
        Assert.True(new FileInfo(Path.Combine(dir, clip.ImageFile)).Length > 1000);
    }

    [E2EFact]
    public void 録画モードでは全編を録り_ステップに動画上の時刻を付ける()
    {
        target = Launch(SampleExe);
        var main = target.MainWindowHandle;
        var engine = new RecorderEngine(target, dir, new Settings { DefaultMode = CaptureTrigger.Click, DrawWaitMs = 300, RecordVideo = true });
        engine.Start();
        Wait(800);
        ClickOn(main, "nameBox");
        Wait(1000);
        ClickOn(main, "okButton");
        Wait(1000);
        engine.ToggleClip(); // 録画モードでは使えない
        Wait(300);

        var session = Stop(engine);
        Assert.Equal("video.mp4", session.VideoFile);
        Assert.All(session.Steps, s => Assert.Null(s.VideoFile));
        var times = session.Steps.Select(s => s.VideoTime!.Value.TotalSeconds).ToList();
        Assert.Equal(2, times.Count);
        Assert.InRange(times[0], 0.5, 1.5);
        Assert.InRange(times[1] - times[0], 0.7, 1.6);
        Assert.InRange(VideoE2ETests.VideoInfo(Path.Combine(dir, "video.mp4")).Duration.TotalSeconds, 2.5, 4.5);
    }

    /// <summary>止めてセッションを保存する。記録中の例外(error.log)があれば失敗させる。</summary>
    Session Stop(RecorderEngine engine)
    {
        var session = engine.Stop();
        var log = Path.Combine(dir, "error.log");
        Assert.False(File.Exists(log), File.Exists(log) ? File.ReadAllText(log) : "");
        SessionStore.Save(session, dir);
        return session;
    }
}
