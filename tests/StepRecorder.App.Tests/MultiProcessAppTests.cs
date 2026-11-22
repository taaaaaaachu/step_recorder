using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Media.Imaging;
using StepRecorder.Core;
using static StepRecorder.App.Tests.Desktop;

namespace StepRecorder.App.Tests;

/// <summary>
/// 1 ウィンドウ(1 タブ)が 1 プロセスのアプリ(サクラエディタなど)。
/// テスト用アプリを 2 つ起動し、片方を隠すのをタブの切り替え、終了させるのをタブを閉じるのに見立てる。入力は送らない。
/// </summary>
public sealed class MultiProcessAppTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "sr-multi-" + Guid.NewGuid());
    readonly List<Process> started = [];

    public void Dispose()
    {
        foreach (var p in started)
            try { p.Kill(); p.WaitForExit(3000); } catch (InvalidOperationException) { }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    /// <summary>color は中身の色(どちらのプロセスが写ったかを見分ける)。</summary>
    (Process Process, nint Window) Tab(string color)
    {
        var p = Open(SampleExe, color);
        started.Add(p);
        Wait(700);
        return (p, p.MainWindowHandle);
    }

    static void SwitchTab(nint from)
    {
        ShowWindowAsync(from, 0 /*SW_HIDE*/);
        Wait(300);
    }

    static Native.POINT CenterOf(nint window)
    {
        var f = Native.FrameBounds(window);
        return new() { X = (f.Left + f.Right) / 2, Y = (f.Top + f.Bottom) / 2 };
    }

    enum Shown { Neither, Red, Lime }

    static Shown What(BitmapSource image)
    {
        var px = VideoE2ETests.Bgr24(image);
        int red = 0, lime = 0;
        for (var i = 0; i < px.Length; i += 3)
        {
            if (px[i + 2] > 200 && px[i + 1] < 60 && px[i] < 60) red++;
            else if (px[i + 1] > 200 && px[i + 2] < 60 && px[i] < 60) lime++;
        }
        return Math.Max(red, lime) < px.Length / 3 / 10 ? Shown.Neither : red > lime ? Shown.Red : Shown.Lime;
    }

    [DesktopFact]
    public void 同じexeの別プロセスへの操作も拾う()
    {
        var a = Tab("Red");
        var target = new AppTarget(a.Process);
        var b = Tab("Lime");

        Assert.True(target.OwnsClick(b.Window, CenterOf(b.Window)));
        Assert.True(target.OwnsKey(b.Window));
        using var other = new ColorWindow(System.Windows.Media.Colors.Blue); // 違う exe(このテスト)
        Assert.False(target.OwnsClick(other.Handle, CenterOf(other.Handle)));
        Assert.False(target.OwnsKey(other.Handle));
    }

    [DesktopFact]
    public void 一覧が古くても_今見えているタブから録る()
    {
        var a = Tab("Red");
        var target = new AppTarget(a.Process); // 一覧を作った時点では a が見えている
        var b = Tab("Lime");
        SwitchTab(a.Window);

        Assert.True(target.IsAvailable);
        Assert.Equal(b.Window, target.InitialWindow);
    }

    [DesktopFact]
    public void 録画中にタブが切り替わったら_見えている方を追う()
    {
        var a = Tab("Red");
        var target = new AppTarget(a.Process);
        var path = Path.Combine(dir, "switch.mp4");

        var rec = target.StartVideo(target.InitialWindow, path, false, () => false);
        Wait(1000);
        var b = Tab("Lime");
        SwitchTab(a.Window);
        Wait(1000);
        rec.Stop();

        using var reader = VideoClips.Reader.Open(path);
        var shown = new List<Shown>();
        for (var t = TimeSpan.Zero; t < reader.Duration; t += TimeSpan.FromSeconds(1.0 / VideoRecorder.Fps)) shown.Add(What(reader.Frame(t)));
        Assert.Equal(Shown.Red, shown[2]);
        Assert.Equal(Shown.Lime, shown[^1]);
        Assert.DoesNotContain(Shown.Neither, shown); // 切り替わりに白いコマも入らない
    }

    [DesktopFact]
    public void 録画中にタブを閉じても_残ったタブを追う()
    {
        var b = Tab("Lime"); // 先に開いておく(閉じるまでは a の下)
        var a = Tab("Red");
        var target = new AppTarget(a.Process);
        var path = Path.Combine(dir, "close.mp4");

        var rec = target.StartVideo(a.Window, path, false, () => false);
        Wait(1000);
        a.Process.Kill();
        a.Process.WaitForExit(3000);
        Wait(1500);
        rec.Stop();

        Assert.Equal(Shown.Lime, What(VideoClips.Frame(path, VideoClips.Info(path).Duration - TimeSpan.FromSeconds(0.2))));
        Assert.True(target.IsAvailable);
        Assert.Equal(b.Window, target.InitialWindow);
    }

    [DesktopFact]
    public void 押したタブが消えていても撮れる()
    {
        var a = Tab("Red");
        var target = new AppTarget(a.Process);
        var b = Tab("Lime");
        a.Process.Kill(); // クリックを処理するまでに閉じていた
        a.Process.WaitForExit(3000);
        Wait(300);

        Assert.True(target.Capture(a.Window, false) is not null, "撮れずにクリックが記録から落ちる");
        Assert.Equal("サンプル画面", target.Describe(a.Window).Title);
    }

    [DesktopFact]
    public void 記録中に切り替わっても手動撮影と全編の動画が残る()
    {
        var a = Tab("Red");
        Directory.CreateDirectory(dir);
        var engine = new RecorderEngine(new AppTarget(a.Process), dir, new Settings { DefaultMode = RecordMode.Video });
        engine.Start();
        Wait(800);
        var b = Tab("Lime");
        SwitchTab(a.Window);
        engine.CaptureManual();
        Wait(800);

        var session = engine.Stop();
        Assert.False(File.Exists(Path.Combine(dir, "error.log")));
        var step = Assert.Single(session.Steps);
        var png = BitmapFrame.Create(new Uri(Path.Combine(dir, step.ImageFile)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.Equal(Shown.Lime, What(png));
        Assert.Equal("video.mp4", session.VideoFile);
    }

    [E2EFact]
    public void タブが切り替わったあとのクリックと入力も記録される()
    {
        var a = Launch(SampleExe, "Red");
        started.Add(a);
        Directory.CreateDirectory(dir);
        var engine = new RecorderEngine(new AppTarget(a), dir, new Settings { DefaultMode = RecordMode.Video });
        engine.Start();
        Wait(800);
        ClickOn(a.MainWindowHandle, "nameBox");
        Wait(700);

        var b = Launch(SampleExe, "Lime");
        started.Add(b);
        SwitchTab(a.MainWindowHandle);
        Wait(700);
        ClickOn(b.MainWindowHandle, "nameBox");
        Wait(700);
        TypeText(b.MainWindowHandle, "xyz");
        Wait(500);

        var session = engine.Stop();
        Assert.False(File.Exists(Path.Combine(dir, "error.log")));
        Assert.Equal(2, session.Steps.Count);
        Assert.Equal("『氏名』欄をクリック\n『氏名』欄に「xyz」と入力", session.Steps[1].Description);
        var video = Path.Combine(dir, session.VideoFile!);
        Assert.Equal(Shown.Lime, What(VideoClips.Frame(video, VideoClips.Info(video).Duration - TimeSpan.FromSeconds(0.2))));
    }

    [DesktopFact]
    public void 対象の一覧は開くたびに新しくなり_選択は残る()
    {
        var app = Open(Path.Combine(AppContext.BaseDirectory, "StepRecorder.exe"));
        started.Add(app);
        var combo = Find(app.MainWindowHandle, "Targets");
        var list = (ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        bool Selected() => ((SelectionPattern)combo.GetCurrentPattern(SelectionPattern.Pattern)).Current.GetSelection() is [{ Current.Name: var name }] && name == "SampleTarget — サンプル画面";

        Tab("Red"); // 開始画面を出したあとに開いたアプリ
        list.Expand();
        Wait(400);
        var item = combo.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "SampleTarget — サンプル画面"));
        Assert.True(item != null, "あとから開いたアプリが一覧に無い");
        ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        list.Collapse();
        Wait(300);

        list.Expand(); // 作り直しても選んだまま
        Wait(400);
        list.Collapse();
        Assert.True(Selected(), "選択が外れた");
    }

    [DllImport("user32.dll")] static extern bool ShowWindowAsync(nint window, int show);
}
