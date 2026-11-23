using StepRecorder.Core;

namespace StepRecorder.Core.Tests;

public class DescriberTests
{
    static Operation Click(string name, string type, MouseButton b = MouseButton.Left, bool dbl = false) =>
        new() { Kind = OperationKind.Click, Element = new(name, type, "", null), Button = b, DoubleClick = dbl, Point = new(10, 20) };

    [Theory]
    [InlineData("保存", "Button", MouseButton.Left, false, "『保存』ボタンをクリック")]
    [InlineData("保存", "Button", MouseButton.Right, false, "『保存』ボタンを右クリック")]
    [InlineData("一覧", "ListItem", MouseButton.Left, true, "『一覧』項目をダブルクリック")]
    [InlineData("ロゴ", "Image", MouseButton.Left, false, "『ロゴ』をクリック")]
    [InlineData("", "Pane", MouseButton.Left, false, "(10, 20) をクリック")]
    public void クリック(string name, string type, MouseButton b, bool dbl, string expected) =>
        Assert.Equal(expected, Describer.Describe(Click(name, type, b, dbl)));

    [Fact]
    public void 入力とキー()
    {
        Assert.Equal("『氏名』欄に「山田太郎」と入力",
            Describer.Describe(new Operation { Kind = OperationKind.TextInput, Element = new("氏名", "Edit", "", null), Text = "山田太郎" }));
        Assert.Equal("入力欄に「abc」と入力",
            Describer.Describe(new Operation { Kind = OperationKind.TextInput, Text = "abc" }));
        Assert.Equal("Ctrl+S を押す", Describer.Describe(new Operation { Kind = OperationKind.KeyPress, Keys = "Ctrl+S" }));
    }
}

public class RecordingTests
{
    static readonly WindowInfo Win = new("メイン", "WindowsForms10", "App");

    static Operation Input(string field, string text) =>
        new() { Kind = OperationKind.TextInput, Element = new(field, "Edit", field, null), Text = text };

    [Fact]
    public void ステップ追加で連番とハイライトと説明文が付く()
    {
        var rec = new Recording(new Session());
        Assert.Equal("images/0001.png", rec.NextImageFile);
        var click = new Operation { Kind = OperationKind.Click, Button = MouseButton.Left, Element = new("OK", "Button", "ok", new(1, 2, 3, 4)) };
        var st = rec.AddStep(CaptureTrigger.Click, Win, rec.NextImageFile, 100, 80, DateTimeOffset.Now, click);

        Assert.Equal(1, st.Id);
        Assert.Equal("メイン", st.ScreenName);
        Assert.Equal([new PixelRect(1, 2, 3, 4)], st.Highlights);
        Assert.Equal("『OK』ボタンをクリック", st.Description);
        Assert.Equal("images/0002.png", rec.NextImageFile);
    }

    [Fact]
    public void 同じ欄への連続入力はまとまり別の欄は分かれる()
    {
        var rec = new Recording(new Session());
        rec.AddStep(CaptureTrigger.Manual, Win, "images/0001.png", 1, 1, DateTimeOffset.Now);
        rec.AddOperation(Input("氏名", "山"));
        rec.AddOperation(Input("氏名", "山田"));
        rec.AddOperation(Input("住所", "東京"));

        var st = rec.Session.Steps[0];
        Assert.Equal(2, st.Operations.Count);
        Assert.Equal("『氏名』欄に「山田」と入力\n『住所』欄に「東京」と入力", st.Description);
    }

    [Fact]
    public void ステップが無いときの操作は捨てる() =>
        Assert.False(new Recording(new Session()).AddOperation(Input("a", "b")));
}

public class ExportTests
{
    static Session Sample()
    {
        var rec = new Recording(new Session { TargetName = "App", CreatedAt = new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero) });
        var main = new WindowInfo("メイン", "W", "App");
        var dlg = new WindowInfo("設定", "D", "App");
        rec.AddStep(CaptureTrigger.Manual, main, "images/0001.png", 200, 100, DateTimeOffset.Now);
        rec.Session.Steps[0].Description = "最初の画面";
        rec.AddStep(CaptureTrigger.Manual, dlg, "images/0002.png", 50, 40, DateTimeOffset.Now);
        rec.Session.Steps[1].Description = "設定を開く";
        rec.AddStep(CaptureTrigger.Manual, main, "images/0003.png", 200, 100, DateTimeOffset.Now);
        rec.Session.Steps[2].Description = "戻る\n終了 <b>";
        rec.Session.Steps[2].Highlights.Add(new(5, 6, 7, 8));
        rec.Session.Steps[0].Crop = new(10, 10, 50, 50);
        return rec.Session;
    }

    [Fact]
    public void 手順モードは撮った順()
    {
        var secs = Export.Sections(Sample(), GroupMode.Sequence);
        Assert.Equal(["メイン", "設定", "メイン"], secs.Select(s => s.Title));
    }

    [Fact]
    public void 画面一覧モードは同じ画面をまとめる()
    {
        var secs = Export.Sections(Sample(), GroupMode.ByScreen);
        Assert.Equal(["メイン", "設定"], secs.Select(s => s.Title));
        Assert.Equal([1, 3], secs[0].Steps.Select(s => s.Id));
        Assert.Equal(["最初の画面", "戻る", "終了 <b>"], secs[0].Lines);
    }

    [Fact]
    public void 画面一覧モードのHtmlでは同じ画面のステップを前後に切り替えられる()
    {
        var html = Export.ToHtml(Sample(), GroupMode.ByScreen);
        // 「メイン」は2ステップなので切り替えボタンが付き、2枚目は隠しておく
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<div class=\"pager\">"));
        Assert.Contains("<span class=\"pos\">1 / 2</span>", html);
        Assert.Contains("<div class=\"page\" data-page=\"0\">", html);
        Assert.Contains("<div class=\"page\" data-page=\"1\" hidden>", html);
        Assert.Contains("<image href=\"images/0003.png\"", html); // 2枚目の画像も載る
        // 説明の行はどのステップのものか分かり、最初のステップの行が強調されている
        Assert.Contains("<li data-page=\"0\" class=\"current\">最初の画面</li>", html);
        Assert.Contains("<li data-page=\"1\">戻る</li>", html);
        // 1ステップだけの画面(設定)には付けない
        Assert.Contains("<li>設定を開く</li>", html);
        // 手順モードでは付けない
        Assert.DoesNotContain("class=\"pager\"", Export.ToHtml(Sample(), GroupMode.Sequence));
    }

    [Fact]
    public void HtmlはトリミングとハイライトをSVGで重ねエスケープする()
    {
        var html = Export.ToHtml(Sample(), GroupMode.Sequence);
        Assert.Contains("viewBox=\"10 10 50 50\"", html);
        Assert.Contains("<image href=\"images/0001.png\" width=\"200\" height=\"100\"/>", html);
        Assert.Contains("<rect x=\"5\" y=\"6\" width=\"7\" height=\"8\"/>", html);
        Assert.Contains("<li>終了 &lt;b&gt;</li>", html);
        Assert.DoesNotContain("<b>", html);
    }

    [Fact]
    public void Htmlはトリミングしている画面にだけ解除ボタンと外側を暗くする覆いを付ける()
    {
        var html = Export.ToHtml(Sample(), GroupMode.Sequence); // 3画面のうち1枚目だけトリミングあり
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<button type=\"button\" class=\"crop-toggle\">トリミングを解除</button>"));
        Assert.Contains("data-crop=\"10 10 50 50\" data-full=\"0 0 200 100\"", html);
        // 画像全体から、トリミング範囲をくり抜いた形
        Assert.Contains("<path class=\"shade\" fill-rule=\"evenodd\" d=\"M0 0H200V100H0Z M10 10h50v50h-50Z\"/>", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "class=\"shade\""));
    }

    [Fact]
    public void 保存して読み戻すと同じ内容()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sr-test-" + Guid.NewGuid());
        try
        {
            var s = Sample();
            s.Steps[0].Operations.Add(new() { Kind = OperationKind.Click, Button = MouseButton.Right, Point = new(1, 2) });
            Export.SaveAll(s, dir, GroupMode.Sequence);

            var json = File.ReadAllText(Path.Combine(dir, SessionStore.FileName));
            Assert.Contains("\"screenName\": \"メイン\"", json); // 日本語はエスケープしない
            Assert.Contains("\"button\": \"right\"", json);

            var back = SessionStore.Load(dir);
            Assert.Equal(3, back.Steps.Count);
            Assert.Equal(new PixelRect(10, 10, 50, 50), back.Steps[0].Crop);
            Assert.Equal(MouseButton.Right, back.Steps[0].Operations[0].Button);
            Assert.Equal("戻る\n終了 <b>", back.Steps[2].Description);
            Assert.False(File.Exists(Path.Combine(dir, "steps.md")));
            Assert.True(File.Exists(Path.Combine(dir, Export.HtmlFile)));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class SessionHistoryTests
{
    [Fact]
    public void 控えた順と逆に戻せる_変更の影響を受けない()
    {
        var s = new Session();
        s.Steps.Add(new Step { Id = 1, Description = "a" });
        var h = new SessionHistory();
        Assert.False(h.CanUndo);

        h.Push(s);
        s.Steps[0].Description = "b";
        h.Push(s);
        s.Steps.Clear();

        Assert.Equal("b", h.Undo()!.Steps[0].Description);
        Assert.Equal("a", h.Undo()!.Steps[0].Description);
        Assert.False(h.CanUndo);
        Assert.Null(h.Undo());
    }

    [Fact]
    public void 上限を超えたら古いものから捨てる()
    {
        var h = new SessionHistory(capacity: 2);
        foreach (var name in new[] { "1", "2", "3" }) h.Push(new Session { TargetName = name });
        Assert.Equal("3", h.Undo()!.TargetName);
        Assert.Equal("2", h.Undo()!.TargetName);
        Assert.Null(h.Undo());
    }
}

public class EvidenceTests
{
    [Fact]
    public void 冒頭の表に取れた項目だけを載せる()
    {
        var s = new Session
        {
            TargetName = "App",
            CreatedAt = new DateTimeOffset(2026, 11, 21, 9, 5, 0, TimeSpan.Zero),
            Evidence = new() { AppFile = "App.exe", AppVersion = "1.2.3", Screen = "1920×1080", ScalePercent = 125, RecorderVersion = "0.5.0" },
        };
        Assert.Equal(
            [("対象アプリ", "App.exe"), ("バージョン", "1.2.3"), ("記録日時", "2026-11-21 09:05"), ("画面", "1920×1080(拡大率 125%)"), ("記録ツール", "step_recorder 0.5.0")],
            Export.EvidenceRows(s));
        Assert.Contains("<tr><th>バージョン</th><td>1.2.3</td></tr>", Export.ToHtml(s, GroupMode.Sequence));
    }
}

public class VideoExportTests
{
    /// <summary>静止画1枚と、録画モードの全編から区間を切り出した動画のステップ1つ。</summary>
    static Session Sample()
    {
        var rec = new Recording(new Session { TargetName = "App", CreatedAt = DateTimeOffset.Now, VideoFile = "video.mp4" });
        var win = new WindowInfo("メイン", "W", "App");
        rec.AddStep(CaptureTrigger.Click, win, "images/0001.png", 200, 100, DateTimeOffset.Now);
        var clip = rec.AddStep(CaptureTrigger.Video, win, "images/0002.png", 200, 100, DateTimeOffset.Now);
        (clip.VideoFile, clip.VideoStart, clip.VideoEnd) = ("video.mp4", TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4));
        clip.Crop = new(10, 20, 100, 50);
        return rec.Session;
    }

    [Fact]
    public void 動画のステップは既定の説明が付き_動画の置き場所が決まる()
    {
        var rec = new Recording(new Session());
        Assert.Equal("videos/0001.mp4", rec.NextVideoFile);
        var st = rec.AddStep(CaptureTrigger.Video, new("設定", "W", "App"), "images/0001.png", 1, 1, DateTimeOffset.Now);
        Assert.Equal("『設定』の操作(動画)", st.Description);
    }

    [Theory]
    [InlineData(null, null, "v.mp4")]
    [InlineData(1.5, null, "v.mp4#t=1.5")]
    [InlineData(null, 4.0, "v.mp4#t=0,4")]
    [InlineData(1.25, 3.5, "v.mp4#t=1.25,3.5")]
    public void 区間はメディアフラグメントで指定する(double? start, double? end, string expected) =>
        Assert.Equal(expected, Export.ClipSource(new Step
        {
            VideoFile = "v.mp4",
            VideoStart = start is { } a ? TimeSpan.FromSeconds(a) : null,
            VideoEnd = end is { } b ? TimeSpan.FromSeconds(b) : null,
        }));

    [Fact]
    public void Htmlでは動画を区間だけ再生しトリミングはCSSで表す()
    {
        var html = Export.ToHtml(Sample(), GroupMode.Sequence);
        Assert.Contains("<video class=\"clip\" controls preload=\"metadata\" src=\"video.mp4#t=1.5,4\" poster=\"images/0002.png\" width=\"100\" data-start=\"1.5\" data-end=\"4\" " +
            "style=\"object-view-box: inset(20px 90px 30px 10px); aspect-ratio: 100 / 50\"></video>", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<svg class=\"shot\"")); // 動画のステップは静止画にしない
    }

    [Fact]
    public void Htmlの動画に速さの選択が付き_ステップの速さが初期値になる()
    {
        var s = Sample();
        var html = Export.ToHtml(s, GroupMode.Sequence);
        Assert.Contains("</video>\n<label class=\"rate\">再生の速さ <select autocomplete=\"off\">", html); // スクリプトが動画の次の要素として探す
        Assert.Contains("<option value=\"0.25\">0.25 倍速</option>", html);
        Assert.Contains("<option value=\"1\" selected>等速</option>", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<label class=\"rate\">")); // 静止画には付かない

        s.Steps[1].VideoSpeed = 1.25; // 手で直した、選択肢に無い速さ
        Assert.Contains("<option value=\"1.25\" selected>1.25 倍速</option><option value=\"1.5\">", Export.ToHtml(s, GroupMode.Sequence));

        s.Steps[1].VideoSpeed = 0.5;
        html = Export.ToHtml(s, GroupMode.Sequence);
        Assert.Contains("<option value=\"0.5\" selected>0.5 倍速</option>", html);
        Assert.DoesNotContain("<option value=\"1\" selected>", html);
        Assert.Contains("requestAnimationFrame(stopAtEnd)", Export.ToHtml(s, GroupMode.Sequence)); // 倍速でも区間を越えない
    }

    [Theory]
    [InlineData(null, 1.0)]
    [InlineData(2.0, 2.0)]
    [InlineData(0.0, 0.25)] // 手で直した session.json。0 のままだと書き出しが終わらない
    [InlineData(-3.0, 0.25)]
    [InlineData(100.0, 8.0)]
    public void 速さは使える範囲に収める(double? saved, double used) => Assert.Equal(used, new Step { VideoSpeed = saved }.Speed);

    [Theory]
    [InlineData(1.0, "等速")]
    [InlineData(0.25, "0.25 倍速")]
    [InlineData(2.0, "2 倍速")]
    public void 速さの表示(double speed, string label) => Assert.Equal(label, PlaybackSpeed.Label(speed));

    [Fact]
    public void 速さは等速なら保存せず_元に戻せる()
    {
        var s = Sample();
        string Json() => System.Text.Json.JsonSerializer.Serialize(s, SessionStore.Options);
        Assert.DoesNotContain("speed", Json(), StringComparison.OrdinalIgnoreCase); // 計算値の Speed も書かない
        var history = new SessionHistory();
        history.Push(s);
        s.Steps[1].VideoSpeed = 2;
        Assert.Contains("\"videoSpeed\": 2", Json());
        Assert.Equal(2, SessionStore.Clone(s).Steps[1].Speed);
        Assert.Equal(1, history.Undo()!.Steps[1].Speed);
    }

    [Theory]
    [InlineData(ClipFormat.Mp4)]
    [InlineData(ClipFormat.Gif)]
    public void 配布用には使っている画像と切り出した動画だけを書き出す(ClipFormat format)
    {
        var src = Path.Combine(Path.GetTempPath(), "sr-src-" + Guid.NewGuid());
        var dst = Path.Combine(Path.GetTempPath(), "sr-dst-" + Guid.NewGuid());
        try
        {
            var s = Sample();
            s.Steps[1].VideoSpeed = 2;
            Directory.CreateDirectory(Path.Combine(src, "images"));
            foreach (var f in new[] { "images/0001.png", "images/0002.png", "video.mp4" }) File.WriteAllText(Path.Combine(src, f), f);
            SessionStore.Save(s, src);

            var calls = new List<(int Id, string Clip, string? Poster)>();
            Export.ExportForDistribution(s, src, dst, GroupMode.Sequence, format, (st, clip, poster) =>
            {
                calls.Add((st.Id, Path.GetRelativePath(dst, clip), poster == null ? null : Path.GetRelativePath(dst, poster)));
                Assert.Equal(TimeSpan.FromSeconds(1.5), st.VideoStart); // 元のステップ(区間・トリミング付き)が渡る
                Assert.Equal(2, st.Speed);
                Directory.CreateDirectory(Path.GetDirectoryName(clip)!);
                File.WriteAllText(clip, "");
            });

            var ext = format == ClipFormat.Gif ? "gif" : "mp4";
            Assert.Equal([(2, Path.Combine("clips", $"0002.{ext}"), format == ClipFormat.Gif ? null : Path.Combine("clips", "0002.png"))], calls);
            Assert.True(File.Exists(Path.Combine(dst, "images", "0001.png")));
            Assert.False(File.Exists(Path.Combine(dst, "images", "0002.png"))); // 動画のポスターは書き出した動画から作る
            Assert.False(File.Exists(Path.Combine(dst, SessionStore.FileName)));
            Assert.False(File.Exists(Path.Combine(dst, "video.mp4")));

            var html = File.ReadAllText(Path.Combine(dst, Export.HtmlFile));
            Assert.Contains(format == ClipFormat.Gif
                ? "<img class=\"clip\" src=\"clips/0002.gif\" width=\"100\" height=\"50\" alt=\"\">"
                : "<video class=\"clip\" controls preload=\"metadata\" src=\"clips/0002.mp4\" poster=\"clips/0002.png\" width=\"100\"></video>", html);
            Assert.False(File.Exists(Path.Combine(dst, "steps.md")));
            if (format == ClipFormat.Gif)
            {
                // 焼き込み済みで、プレーヤーも無い
                Assert.DoesNotContain("<label class=\"rate\">", html);
            }
            else
            {
                Assert.Contains("<option value=\"2\" selected>2 倍速</option>", html);
            }
            Assert.Equal(2, s.Steps[1].Speed);
            Assert.Equal("video.mp4", s.VideoFile); // 元のセッションは変えない
        }
        finally
        {
            if (Directory.Exists(src)) Directory.Delete(src, true);
            if (Directory.Exists(dst)) Directory.Delete(dst, true);
        }
    }

    [Fact]
    public void 動画の項目も保存して読み戻せる()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sr-test-" + Guid.NewGuid());
        try
        {
            var s = Sample();
            s.Steps[0].VideoTime = TimeSpan.FromSeconds(0.8);
            SessionStore.Save(s, dir);
            var back = SessionStore.Load(dir);
            Assert.Equal("video.mp4", back.VideoFile);
            Assert.Equal(TimeSpan.FromSeconds(0.8), back.Steps[0].VideoTime);
            Assert.Equal((TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4)), (back.Steps[1].VideoStart!.Value, back.Steps[1].VideoEnd!.Value));
            Assert.Equal(CaptureTrigger.Video, back.Steps[1].Trigger);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class TimelineTests
{
    static Step At(double? sec) => new() { VideoTime = sec is { } s ? TimeSpan.FromSeconds(s) : null };

    [Theory]
    [InlineData(0.5, 0)] // どれよりも前
    [InlineData(1.0, 1)] // 同じ時刻なら後ろ
    [InlineData(2.5, 2)] // 2.0 の直後(時刻の無いステップより前)
    [InlineData(9.0, 4)] // 最後
    public void 足したステップは動画上の時刻の順に入れる(double time, int expected) =>
        Assert.Equal(expected, Recording.IndexByVideoTime([At(1), At(2), At(null), At(3)], TimeSpan.FromSeconds(time)));
}
