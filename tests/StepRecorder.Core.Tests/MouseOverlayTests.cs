using System.Text.Json;

namespace StepRecorder.Core.Tests;

public class MouseOverlayTests
{
    static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);
    static MouseStroke M(double at, MouseAction a, int x = 10, int y = 20) => new(S(at), x, y, a);

    [Fact]
    public void クリックは1回ずつ_ホイールは同じ向きが続く間を1つにまとめる()
    {
        var marks = MouseOverlay.Marks([
            M(1, MouseAction.Left), M(1.1, MouseAction.Left), // ダブルクリックは輪が 2 つ
            M(2, MouseAction.WheelDown, 50, 60), M(2.3, MouseAction.WheelDown, 55, 65), M(2.6, MouseAction.WheelDown),
            M(2.7, MouseAction.WheelUp), // 向きが変われば別
            M(4, MouseAction.WheelDown), // 間が空けば別
        ]);
        Assert.Equal([
            new MouseMark(S(1), S(1.6), 10, 20, MouseAction.Left),
            new MouseMark(S(1.1), S(1.7), 10, 20, MouseAction.Left),
            new MouseMark(S(2), S(3.2), 50, 60, MouseAction.WheelDown), // 位置は回し始め
            new MouseMark(S(2.7), S(3.3), 10, 20, MouseAction.WheelUp),
            new MouseMark(S(4), S(4.6), 10, 20, MouseAction.WheelDown),
        ], marks);
    }

    [Theory]
    [InlineData(100, 100, false, false)] // ふつうは右下
    [InlineData(630, 100, true, false)] // 右端(閉じるボタンなど)は左に
    [InlineData(100, 355, false, true)] // 下端は上に
    [InlineData(630, 355, true, true)]
    public void マウスの絵は見えている範囲からはみ出さない側に置く(double x, double y, bool left, bool up)
    {
        Assert.Equal((left, up), MouseOverlay.PicSide(x, y, 16, 640, 360));
    }

    [Fact]
    public void クリックの輪は時間とともに広がり_ホイールは段階を持たない()
    {
        var marks = MouseOverlay.Marks([M(1, MouseAction.Right), M(1, MouseAction.WheelLeft)]);
        Assert.Empty(MouseOverlay.Visible(marks, S(0.99)));
        Assert.Equal([0, 0], MouseOverlay.Visible(marks, S(1)).Select(v => v.Phase));
        Assert.Equal([3, 0], MouseOverlay.Visible(marks, S(1.3)).Select(v => v.Phase));
        Assert.Equal([MouseOverlay.Phases - 1, 0], MouseOverlay.Visible(marks, S(1.599)).Select(v => v.Phase));
        Assert.Empty(MouseOverlay.Visible(marks, S(1.6)));
    }

    static MouseStroke Dragged(params (double At, int X, int Y)[] path) =>
        new(S(1), 10, 20, MouseAction.Left) { Drag = path.Select(p => new MousePoint(S(p.At), p.X, p.Y)).ToList() };

    [Fact]
    public void ドラッグ中はマウスの絵が動いた所についていき_輪は押した所で消え_離すまで出す()
    {
        var marks = MouseOverlay.Marks([Dragged((1.2, 50, 20), (1.5, 90, 40), (2, 120, 60))]);
        Assert.Equal(S(2), Assert.Single(marks).End);
        Assert.Equal(new MouseShown(marks[0], 1, 10, 20), Assert.Single(MouseOverlay.Visible(marks, S(1.1)))); // 動かす前は押した所
        Assert.Equal(new MouseShown(marks[0], 3, 50, 20), Assert.Single(MouseOverlay.Visible(marks, S(1.3))));
        Assert.Equal(new MouseShown(marks[0], null, 90, 40), Assert.Single(MouseOverlay.Visible(marks, S(1.7)))); // 輪は消え、絵だけ
        Assert.Empty(MouseOverlay.Visible(marks, S(2)));
    }

    [Fact]
    public void 輪が消える前に離したドラッグは_輪が消えるまで出す()
    {
        var mark = Assert.Single(MouseOverlay.Marks([Dragged((1.1, 50, 20), (1.2, 60, 20))]));
        Assert.Equal(S(1.6), mark.End);
        Assert.Equal((60, 20), mark.PicAt(S(1.5)));
    }

    [Fact]
    public void ステップに重ねるのは区間の中だけで_キーとは別に出さない設定にできる()
    {
        var session = new Session { VideoFile = "video.mp4", Mouse = [M(1, MouseAction.Left), M(5, MouseAction.Middle)], Keys = [new(S(5), "a", true)] };
        var range = new Step { VideoFile = "video.mp4", VideoStart = S(4), VideoEnd = S(9) };
        var clip = new Step { VideoFile = "videos/0002.mp4", Mouse = [M(0.5, MouseAction.WheelUp)] };

        Assert.Equal([MouseAction.Middle], MouseOverlay.StrokesFor(session, range).Select(m => m.Action));
        Assert.Equal([MouseAction.WheelUp], MouseOverlay.StrokesFor(session, clip).Select(m => m.Action));
        range.HideMouse = true;
        Assert.Empty(MouseOverlay.StrokesFor(session, range));
        Assert.Single(MouseOverlay.Recorded(session, range));
        Assert.Single(KeyOverlay.StrokesFor(session, range)); // キーは出したまま
    }

    [Fact]
    public void マウスと表示の設定は読み書きでき_出すときは書かない()
    {
        var session = new Session { VideoFile = "video.mp4", Mouse = [M(1.25, MouseAction.WheelRight, 3, 4)] };
        session.Steps.Add(new Step { VideoFile = "video.mp4" });
        session.Steps.Add(new Step { VideoFile = "video.mp4", HideMouse = true });
        var json = JsonSerializer.Serialize(session, SessionStore.Options);
        var back = SessionStore.Clone(session);

        Assert.Equal(session.Mouse, back.Mouse);
        Assert.Equal([false, true], back.Steps.Select(s => s.HideMouse));
        Assert.Contains("\"wheelRight\"", json);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "\"hideMouse\""));
    }

    [Fact]
    public void ドラッグの位置は読み書きでき_ドラッグしていなければ書かない()
    {
        var session = new Session { VideoFile = "video.mp4", Mouse = [Dragged((1.5, 90, 40)), M(3, MouseAction.Left)] };
        var json = JsonSerializer.Serialize(session, SessionStore.Options);
        var back = SessionStore.Clone(session);

        Assert.Equal([new MousePoint(S(1.5), 90, 40)], back.Mouse![0].Drag);
        Assert.Null(back.Mouse[1].Drag);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "\"drag\""));
    }
}

public class MouseOverlayHtmlTests
{
    static Session WithMouse()
    {
        var s = new Session { TargetName = "App", VideoFile = "video.mp4", Mouse = [new(TimeSpan.FromSeconds(2), 300, 200, MouseAction.Right), new(TimeSpan.FromSeconds(9), 1, 1, MouseAction.Left)] };
        s.Steps.Add(new Step { Id = 1, ImageFile = "images/0001.png", ImageWidth = 640, ImageHeight = 360, VideoFile = "video.mp4", VideoStart = TimeSpan.FromSeconds(1), VideoEnd = TimeSpan.FromSeconds(5), Crop = new(100, 50, 400, 300) });
        return s;
    }

    [Fact]
    public void 編集用のHTMLでは区間の中のマウスを_トリミングの範囲と一緒に渡す()
    {
        var html = Export.ToHtml(WithMouse(), GroupMode.Sequence);
        var clip = html.Split('\n').Single(l => l.Contains("<video"));
        Assert.Contains("data-mouse=\"[[2,2.6,300,200,1]]\" data-mouse-area=\"100 50 400 300\" data-mouse-radius=\"13.33\"", clip); // 半径は見えている高さ(300)で決まる
        Assert.StartsWith("<div class=\"clip-wrap\">", clip);
        Assert.EndsWith("<canvas class=\"mouse\"></canvas></div>", clip); // キーが無ければキーの欄は無し
        Assert.DoesNotContain("PHASES", html);
        Assert.DoesNotContain("RIPPLE", html);
    }

    [Fact]
    public void ドラッグは秒と位置を並べて渡す()
    {
        var s = WithMouse();
        s.Mouse![0].Drag = [new(TimeSpan.FromSeconds(2.5), 320, 210), new(TimeSpan.FromSeconds(3), 340, 220)];
        var clip = Export.ToHtml(s, GroupMode.Sequence).Split('\n').Single(l => l.Contains("<video"));
        Assert.Contains("data-mouse=\"[[2,3,300,200,1,[2.5,320,210,3,340,220]]]\"", clip); // 離すまで出す
    }

    [Fact]
    public void 出さない設定のステップと_焼き込んだ配布用には重ねない()
    {
        var hidden = WithMouse();
        hidden.Steps[0].HideMouse = true;
        var html = Export.ToHtml(hidden, GroupMode.Sequence);
        Assert.DoesNotContain("data-mouse=", html);
        Assert.DoesNotContain("clip-wrap\">", html);

        var dir = Path.Combine(Path.GetTempPath(), "sr-mouse-html-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(dir);
            Export.ExportForDistribution(WithMouse(), dir, dir, GroupMode.Sequence, ClipFormat.Mp4, (_, _, _) => { });
            Assert.DoesNotContain("data-mouse=", File.ReadAllText(Path.Combine(dir, Export.HtmlFile)));
        }
        finally { Directory.Delete(dir, true); }
    }
}
