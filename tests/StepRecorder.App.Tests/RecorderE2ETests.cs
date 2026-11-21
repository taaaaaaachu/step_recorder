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
