using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using StepRecorder.Core;
using Geometry = StepRecorder.Core.Geometry;
using static StepRecorder.App.Tests.Desktop;

namespace StepRecorder.App.Tests;

/// <summary>
/// モニターを対象に実際に操作して記録する。実際の画面が写らないよう、背面を一色のウィンドウで覆い、その上にテスト用アプリを出す。
/// </summary>
public sealed class MonitorRecorderE2ETests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "sr-monitor-e2e-" + Guid.NewGuid());
    readonly List<ColorWindow> covers = [];
    Process? sample;
    RecorderEngine? engine;

    public void Dispose()
    {
        engine?.Stop();
        try { sample?.Kill(); } catch (InvalidOperationException) { }
        foreach (var cover in covers) cover.Dispose();
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    /// <summary>全モニターを覆ってからテスト用アプリを前面に出し、それが載っているモニターを返す。</summary>
    DisplayMonitor LaunchCovered()
    {
        foreach (var m in Monitors.All())
        {
            var cover = new ColorWindow(Colors.Blue, m.Bounds.X, m.Bounds.Y, m.Bounds.Width, m.Bounds.Height);
            cover.Invoke(w => w.Topmost = false);
            covers.Add(cover);
        }
        sample = Launch(SampleExe);
        var on = Native.MonitorFromWindow(sample.MainWindowHandle, Native.MONITOR_DEFAULTTONEAREST);
        return Monitors.All().Single(m => m.Handle == on);
    }

    RecorderEngine Record(DisplayMonitor monitor, RecordMode mode)
    {
        Directory.CreateDirectory(dir);
        engine = new RecorderEngine(new MonitorTarget(monitor), dir, new Settings { DefaultMode = mode });
        engine.Start();
        return engine;
    }

    Session Stop()
    {
        var session = engine!.Stop();
        engine = null;
        var log = Path.Combine(dir, "error.log");
        Assert.False(File.Exists(log), File.Exists(log) ? File.ReadAllText(log) : "");
        return session;
    }

    [E2EFact]
    public void 録画モードでは全編を録り_操作したウィンドウで切ったステップができ_配布用に書き出せる()
    {
        var m = LaunchCovered();
        var window = sample!.MainWindowHandle;
        var frame = Native.FrameBounds(window);
        Record(m, RecordMode.Video);
        Wait(1000);
        ClickOn(window, "nameBox");
        Wait(600);
        TypeText(window, "abc");
        Wait(1000);

        var session = Stop();
        Assert.Equal((m.Name, "video.mp4"), (session.TargetName, session.VideoFile));
        Assert.Equal("", session.Evidence.AppFile);
        var step = Assert.Single(session.Steps);
        Assert.Equal((m.Bounds.Width, m.Bounds.Height), (step.ImageWidth, step.ImageHeight));
        Assert.Equal(("サンプル画面", "SampleTarget"), (step.ScreenName, step.Window.ProcessName));
        Assert.Equal("『氏名』欄をクリック\n『氏名』欄に「abc」と入力", step.Description);
        Assert.Equal(new PixelRect(frame.Left - m.Bounds.X, frame.Top - m.Bounds.Y, frame.Width, frame.Height), step.Crop);
        var mark = Assert.Single(step.Highlights); // 強調枠もモニター内の座標
        Assert.True(Geometry.Contains(step.Crop!, mark.X, mark.Y) && Geometry.Contains(step.Crop!, mark.X + mark.Width, mark.Y + mark.Height));
        Assert.InRange(step.VideoTime!.Value.TotalSeconds, 0.5, 1.8);

        var video = Path.Combine(dir, session.VideoFile!);
        var (duration, w, h) = VideoE2ETests.VideoInfo(video);
        Assert.Equal((m.Bounds.Width & ~1, m.Bounds.Height & ~1), (w, h));

        // 編集画面の「区間を動画ステップに追加」と同じ形のステップを足して書き出す
        var rec = new Recording(session);
        var file = rec.NextImageFile;
        WindowCapture.SavePng(VideoClips.Frame(video, TimeSpan.FromSeconds(0.5)), Path.Combine(dir, file));
        var clip = rec.AddStep(CaptureTrigger.Video, step.Window, file, w, h, DateTimeOffset.Now);
        (clip.VideoFile, clip.VideoStart, clip.VideoEnd) = (session.VideoFile, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(Math.Min(2, duration.TotalSeconds)));
        foreach (var format in new[] { ClipFormat.Mp4, ClipFormat.Gif })
        {
            var outDir = Path.Combine(dir, $"out-{format}");
            Directory.CreateDirectory(outDir);
            Export.ExportForDistribution(session, dir, outDir, GroupMode.Sequence, format, (st, path, poster) => VideoClips.ExportClip(dir, st, format, path, poster));
            Assert.True(File.Exists(Path.Combine(outDir, Export.HtmlFile)));
            Assert.True(new FileInfo(Path.Combine(outDir, "clips", $"{clip.Id:D4}.{(format == ClipFormat.Gif ? "gif" : "mp4")}")).Length > 1000);
        }
    }

    [E2EFact]
    public void クリックごと_手動_録画ボタンもモニターで使える()
    {
        var m = LaunchCovered();
        var window = sample!.MainWindowHandle;
        var recorder = Record(m, RecordMode.Click);
        Wait(300);

        ClickOn(window, "nameBox");
        Wait(800);
        recorder.ToggleClip();
        Wait(1500);
        ClickOn(window, "nameBox"); // 録画中は撮らずに操作だけ
        Wait(1000);
        recorder.ToggleClip();
        Wait(1500);
        recorder.Mode = RecordMode.Manual;
        ClickOn(window, "nameBox"); // 手動では撮らない
        Wait(500);
        recorder.CaptureManual();
        Wait(800);

        var session = Stop();
        Assert.Equal([CaptureTrigger.Click, CaptureTrigger.Video, CaptureTrigger.Manual], session.Steps.Select(s => s.Trigger));
        Assert.All(session.Steps, s => Assert.Equal((m.Bounds.Width & ~1, m.Bounds.Height & ~1), (s.ImageWidth & ~1, s.ImageHeight & ~1)));
        Assert.Equal("『氏名』欄をクリック", session.Steps[1].Description);
        Assert.NotNull(session.Steps[2].Crop);
        Assert.Null(session.VideoFile);
    }

    [E2EFact]
    public void 他のモニターでの操作は記録しない()
    {
        var on = LaunchCovered();
        if (Monitors.All().FirstOrDefault(m => m.Handle != on.Handle) is not { } other) return; // モニターが 1 枚なら確かめようがない
        var window = sample!.MainWindowHandle;
        var recorder = Record(other, RecordMode.Click);
        recorder.CaptureManual(); // 操作が足される先
        Wait(800);
        ClickOn(window, "nameBox");
        Wait(400);
        TypeText(window, "xyz");
        Wait(2000);

        var step = Assert.Single(Stop().Steps);
        Assert.Equal(CaptureTrigger.Manual, step.Trigger);
        Assert.Equal((other.Bounds.Width, other.Bounds.Height), (step.ImageWidth, step.ImageHeight)); // 前面が他のモニターでも撮れる
        Assert.Empty(step.Operations);
    }
}
