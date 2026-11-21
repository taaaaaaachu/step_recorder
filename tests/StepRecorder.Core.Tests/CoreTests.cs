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
    public void 入力とキーと画面表示()
    {
        Assert.Equal("『氏名』欄に「山田太郎」と入力",
            Describer.Describe(new Operation { Kind = OperationKind.TextInput, Element = new("氏名", "Edit", "", null), Text = "山田太郎" }));
        Assert.Equal("入力欄に「abc」と入力",
            Describer.Describe(new Operation { Kind = OperationKind.TextInput, Text = "abc" }));
        Assert.Equal("Ctrl+S を押す", Describer.Describe(new Operation { Kind = OperationKind.KeyPress, Keys = "Ctrl+S" }));
        Assert.Equal("『設定』画面が表示された",
            Describer.Describe(new Operation { Kind = OperationKind.WindowShown, Element = new("設定", "Window", "", null) }));
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
        var rec = new Recording(new Session { ProcessName = "App", CreatedAt = new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero) });
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
    public void 画面一覧モードは同じ画面をまとめ先頭の画像を使う()
    {
        var secs = Export.Sections(Sample(), GroupMode.ByScreen);
        Assert.Equal(["メイン", "設定"], secs.Select(s => s.Title));
        Assert.Equal(1, secs[0].Image.Id);
        Assert.Equal(["最初の画面", "戻る", "終了 <b>"], secs[0].Lines);
    }

    [Fact]
    public void Markdown()
    {
        var md = Export.ToMarkdown(Sample(), GroupMode.ByScreen);
        Assert.Equal("""
            # App 画面一覧 (2026-11-21)

            ## 1. メイン

            ![メイン](images/0001.png)

            1. 最初の画面
            2. 戻る
            3. 終了 <b>

            ## 2. 設定

            ![設定](images/0002.png)

            1. 設定を開く

            """.ReplaceLineEndings("\n"), md);
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
            Assert.True(File.Exists(Path.Combine(dir, Export.MarkdownFile)));
            Assert.True(File.Exists(Path.Combine(dir, Export.HtmlFile)));
        }
        finally { Directory.Delete(dir, true); }
    }
}
