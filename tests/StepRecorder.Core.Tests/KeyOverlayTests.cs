using System.Text.Json;

namespace StepRecorder.Core.Tests;

public class KeyOverlayTests
{
    static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);
    static KeyStroke Char(double at, string text) => new(S(at), text, true);
    static KeyStroke Key(double at, string text) => new(S(at), text, false);

    [Fact]
    public void 続けて打った文字は1つにまとめ_間が空けば分ける()
    {
        var chips = KeyOverlay.Chips([Char(1, "a"), Char(1.3, "b"), Char(2.2, "c"), Char(3.5, "d")]);
        Assert.Equal([new KeyChip(S(1), S(3.7), "abc"), new KeyChip(S(3.5), S(5), "d")], chips);
    }

    [Fact]
    public void ショートカットは文字の続きにせず_同じキーの連打は回数にする()
    {
        var chips = KeyOverlay.Chips([Char(0, "x"), Key(0.2, "Ctrl+S"), Key(1, "←"), Key(1.4, "←"), Key(1.8, "←"), Key(3, "←")]);
        Assert.Equal(["x", "Ctrl+S", "← ×3", "←"], chips.Select(c => c.Text));
        Assert.Equal(S(1.8) + KeyOverlay.Linger, chips[2].End);
    }

    [Fact]
    public void 空白だけならSpace_長い入力は末尾だけ()
    {
        Assert.Equal("Space", KeyOverlay.Chips([Char(0, " ")])[0].Text);
        var long_ = KeyOverlay.Chips([.. Enumerable.Range(0, 30).Select(i => Char(i * 0.1, ((char)('a' + i % 26)).ToString()))])[0].Text;
        Assert.Equal(1 + KeyOverlay.MaxRunLength, long_.Length);
        Assert.StartsWith("…", long_);
        Assert.EndsWith("d", long_);
    }

    [Fact]
    public void その時刻に出す表示は新しい3つまで()
    {
        var chips = KeyOverlay.Chips([Key(0, "F1"), Key(0.1, "F2"), Key(0.2, "F3"), Key(0.3, "F4")]);
        Assert.Empty(KeyOverlay.Visible(chips, S(-0.1)));
        Assert.Equal(["F2", "F3", "F4"], KeyOverlay.Visible(chips, S(0.5)));
        Assert.Equal(["F4"], KeyOverlay.Visible(chips, S(1.75)));
        Assert.Empty(KeyOverlay.Visible(chips, S(1.8)));
    }

    [Fact]
    public void ステップに重ねるキーは_区間の中だけ_出さない設定なら無し()
    {
        var session = new Session { VideoFile = "video.mp4", Keys = [Key(1, "A"), Key(5, "B"), Key(9, "C")] };
        var range = new Step { VideoFile = "video.mp4", VideoStart = S(4), VideoEnd = S(9) };
        var clip = new Step { VideoFile = "videos/0002.mp4", Keys = [Key(0.5, "Z")] };

        Assert.Equal(["B"], KeyOverlay.StrokesFor(session, range).Select(k => k.Text));
        Assert.Equal(["Z"], KeyOverlay.StrokesFor(session, clip).Select(k => k.Text));
        range.HideKeys = true;
        Assert.Empty(KeyOverlay.StrokesFor(session, range));
        Assert.Equal(["B"], KeyOverlay.Recorded(session, range).Select(k => k.Text)); // 切り替えのチェックを出すかどうかに使う
        Assert.Empty(KeyOverlay.StrokesFor(session, new Step { ImageFile = "images/0001.png" }));
    }

    [Fact]
    public void キーと表示の設定は読み書きでき_出すときは書かない()
    {
        var session = new Session { VideoFile = "video.mp4", Keys = [Char(1.25, "あ")] };
        session.Steps.Add(new Step { VideoFile = "video.mp4" });
        session.Steps.Add(new Step { VideoFile = "video.mp4", HideKeys = true });
        var json = JsonSerializer.Serialize(session, SessionStore.Options);
        var back = SessionStore.Clone(session);

        Assert.Equal(session.Keys, back.Keys);
        Assert.Equal([false, true], back.Steps.Select(s => s.HideKeys));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "\"hideKeys\"")); // 既定の false は書かない
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "\"keys\"")); // キーの無いステップには書かない
    }

    [Fact]
    public void 重ねる画素は透明なら元のまま_不透明なら置き換え_半透明なら混ぜる()
    {
        var dst = new byte[3 * 1 * 4];
        Array.Fill(dst, (byte)200);
        byte[] src = [0, 0, 0, 0, /**/ 10, 20, 30, 255, /**/ 50, 50, 50, 128];
        KeyOverlay.Blend(dst, 3, 1, src, 3, 1, 0, 0);
        Assert.Equal([200, 200, 200], dst[0..3]);
        Assert.Equal([10, 20, 30], dst[4..7]);
        Assert.Equal((byte)(50 + (200 * 127 + 127) / 255), dst[8]);

        Array.Fill(dst, (byte)200);
        KeyOverlay.Blend(dst, 3, 1, src, 3, 1, -2, 0); // 左にはみ出す。src の 3 列目だけが dst の 1 列目に載る
        Assert.Equal((byte)(50 + (200 * 127 + 127) / 255), dst[0]);
        Assert.Equal([200, 200, 200, 200, 200, 200], dst[4..10]);
    }

    [Theory]
    [InlineData(0x53, true, false, false, false, null, "Ctrl+S", false)]
    [InlineData(0xBA, true, false, true, false, ';', "Ctrl+Shift+;", false)]
    [InlineData(0x41, false, false, false, false, 'a', "a", true)]
    [InlineData(0x41, false, false, true, false, 'A', "A", true)]
    [InlineData(0x31, false, false, true, false, '!', "!", true)]
    [InlineData(0x0D, false, false, false, false, '\r', "Enter", false)]
    [InlineData(0x09, false, false, true, false, '\t', "Shift+Tab", false)]
    [InlineData(0x25, false, false, false, false, null, "←", false)]
    [InlineData(0x20, false, false, false, false, ' ', " ", true)]
    [InlineData(0x74, false, false, false, false, null, "F5", false)]
    [InlineData(0xA6, false, false, false, false, null, "ブラウザーの戻る", false)]
    [InlineData(0xAE, false, false, false, false, null, "音量 −", false)]
    public void キーの表記(int vk, bool ctrl, bool alt, bool shift, bool win, char? typed, string text, bool isChar)
    {
        Assert.Equal((text, isChar), KeyLabel.Of(vk, ctrl, alt, shift, win, typed));
    }

    [Theory]
    [InlineData(0x10)] // Shift
    [InlineData(0x11)] // Ctrl
    [InlineData(0xA4)] // 左 Alt
    [InlineData(0x5B)] // 左 Win
    [InlineData(0xFF)] // 名前の無いキー
    public void 修飾キーだけや名前の無いキーは出さない(int vk) => Assert.Null(KeyLabel.Of(vk, false, false, false, false, null));
}

public class KeyOverlayHtmlTests
{
    static Session WithKeys()
    {
        var s = new Session { TargetName = "App", VideoFile = "video.mp4", Keys = [new(TimeSpan.FromSeconds(2), "Ctrl+S", false), new(TimeSpan.FromSeconds(9), "<b>", true)] };
        s.Steps.Add(new Step { Id = 1, ImageFile = "images/0001.png", ImageWidth = 640, ImageHeight = 360, VideoFile = "video.mp4", VideoStart = TimeSpan.FromSeconds(1), VideoEnd = TimeSpan.FromSeconds(5) });
        return s;
    }

    [Fact]
    public void 編集用のHTMLでは区間の中のキーを再生に合わせて重ねる()
    {
        var html = Export.ToHtml(WithKeys(), GroupMode.Sequence);
        var clip = html.Split('\n').Single(l => l.Contains("<video"));
        // 区間の外(9 秒)のキーは入れない。時刻は動画ファイルの時刻
        Assert.Equal("<div class=\"clip-wrap\"><video class=\"clip\" controls preload=\"metadata\" src=\"video.mp4#t=1,5\" poster=\"images/0001.png\" width=\"640\" data-start=\"1\" data-end=\"5\" "
            + "data-keys=\"[[2,3.5,&quot;Ctrl+S&quot;]]\" data-key-size=\"20\" data-key-rows=\"360\"></video><div class=\"keys\"></div></div>", clip);
        Assert.Contains("bottom: max(0.8em, 56px)", html); // 言語の設定に関わらず小数点
        Assert.DoesNotContain("MAX_KEYS", html);
    }

    [Fact]
    public void 出さない設定のステップと_焼き込んだ配布用には重ねない()
    {
        var hidden = WithKeys();
        hidden.Steps[0].HideKeys = true;
        Assert.DoesNotContain("data-keys=", Export.ToHtml(hidden, GroupMode.Sequence));

        var dir = Path.Combine(Path.GetTempPath(), "sr-keys-html-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(dir);
            Export.ExportForDistribution(WithKeys(), dir, dir, GroupMode.Sequence, ClipFormat.Mp4, (_, _, _) => { });
            Assert.DoesNotContain("data-keys=", File.ReadAllText(Path.Combine(dir, Export.HtmlFile)));
        }
        finally { Directory.Delete(dir, true); }
    }
}
