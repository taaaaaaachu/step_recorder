using StepRecorder.Core;

namespace StepRecorder.Core.Tests;

public class KeyClassifierTests
{
    [Theory]
    [InlineData(0x53, true, false, false, false, false, KeyKind.Shortcut, "Ctrl+S")]
    [InlineData(0x53, true, false, true, false, false, KeyKind.Shortcut, "Ctrl+Shift+S")]
    [InlineData(0x73, false, true, false, false, false, KeyKind.Shortcut, "Alt+F4")]
    [InlineData(0x74, false, false, false, false, false, KeyKind.Shortcut, "F5")]
    [InlineData(0x0D, false, false, false, false, false, KeyKind.Shortcut, "Enter")]
    [InlineData(0x0D, false, false, false, false, true, KeyKind.Typing, "")] // IME の確定
    [InlineData(0x09, false, false, true, false, true, KeyKind.Shortcut, "Shift+Tab")]
    [InlineData(0x41, false, false, false, false, false, KeyKind.Typing, "")]
    [InlineData(0x41, false, false, true, false, false, KeyKind.Typing, "")] // 大文字入力
    [InlineData(0x08, false, false, false, false, false, KeyKind.Typing, "")]
    [InlineData(0x25, false, false, false, false, false, KeyKind.Ignore, "")] // ← はカーソル移動だけ
    [InlineData(0x24, false, false, true, false, false, KeyKind.Ignore, "")] // Shift+Home は範囲選択
    [InlineData(0x25, true, false, false, false, false, KeyKind.Shortcut, "Ctrl+←")]
    [InlineData(0x20, false, false, false, false, false, KeyKind.Typing, "")] // Space は文字
    [InlineData(0x11, true, false, false, false, false, KeyKind.Ignore, "")] // Ctrl 単体
    [InlineData(0xA2, true, false, false, false, false, KeyKind.Ignore, "")] // 左 Ctrl
    [InlineData(0xBA, true, false, false, false, false, KeyKind.Ignore, "")] // 名前の無いキー + Ctrl
    public void 分類(int vk, bool ctrl, bool alt, bool shift, bool win, bool ime, KeyKind kind, string name) =>
        Assert.Equal((kind, name), KeyClassifier.Classify(vk, ctrl, alt, shift, win, ime));
}

public class ClickRulesTests
{
    static readonly DateTimeOffset T = DateTimeOffset.UnixEpoch;
    static ClickEvent C(int ms, int x, MouseButton b = MouseButton.Left) => new(T.AddMilliseconds(ms), new(x, 0), b);

    [Fact]
    public void ダブルクリック判定()
    {
        Assert.False(ClickRules.IsDoubleClick(null, C(0, 0), 500, 4, 4));
        Assert.True(ClickRules.IsDoubleClick(C(0, 0), C(300, 3), 500, 4, 4));
        Assert.False(ClickRules.IsDoubleClick(C(0, 0), C(600, 0), 500, 4, 4)); // 遅い
        Assert.False(ClickRules.IsDoubleClick(C(0, 0), C(100, 10), 500, 4, 4)); // 離れている
        Assert.False(ClickRules.IsDoubleClick(C(0, 0), C(100, 0, MouseButton.Right), 500, 4, 4));
    }
}

public class HotkeyTests
{
    [Fact]
    public void 解析と表記()
    {
        Assert.True(Hotkey.TryParse("ctrl + Shift + F9", out var h));
        Assert.Equal(HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, h.Modifiers);
        Assert.Equal("F9", h.Key);
        Assert.Equal("Ctrl+Shift+F9", h.ToString());
        Assert.False(Hotkey.TryParse("F9", out _));
        Assert.False(Hotkey.TryParse("Hyper+F9", out _));
    }
}

public class SettingsTests
{
    [Fact]
    public void 保存と読み込み_壊れていたら既定値()
    {
        var path = Path.Combine(Path.GetTempPath(), "sr-settings-" + Guid.NewGuid(), "settings.json");
        try
        {
            new Settings { DefaultMode = RecordMode.Video, GroupMode = GroupMode.ByScreen }.Save(path);
            var s = Settings.Load(path);
            Assert.Equal(RecordMode.Video, s.DefaultMode);
            Assert.Equal(GroupMode.ByScreen, s.GroupMode);
            Assert.True(s.AutoUpdate); // 既定は自動更新オン

            new Settings { AutoUpdate = false }.Save(path);
            Assert.False(Settings.Load(path).AutoUpdate);
            Assert.False(s.CaptureCursor); // 既定はカーソルを写さない
            new Settings { CaptureCursor = true }.Save(path);
            Assert.True(Settings.Load(path).CaptureCursor);

            File.WriteAllText(path, "{ broken");
            Assert.Equal(RecordMode.Click, Settings.Load(path).DefaultMode);
            Assert.Equal(RecordMode.Click, Settings.Load(path + ".none").DefaultMode);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void 変更は今のファイルに対して行い_他の項目を戻さない()
    {
        var path = Path.Combine(Path.GetTempPath(), "sr-settings-" + Guid.NewGuid(), "settings.json");
        try
        {
            new Settings { ClipFormat = ClipFormat.Gif }.Save(path);
            Settings.Update(path, s => s.ClipFormat = ClipFormat.Mp4);
            var latest = Settings.Update(path, s => s.CaptureCursor = true);

            Assert.Equal((ClipFormat.Mp4, true), (latest.ClipFormat, latest.CaptureCursor));
            Assert.Equal((ClipFormat.Mp4, true), (Settings.Load(path).ClipFormat, Settings.Load(path).CaptureCursor));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void フォルダ名() =>
        Assert.Equal("20261121-174100_a_b", Settings.SessionFolderName(new DateTimeOffset(2026, 11, 21, 17, 41, 0, TimeSpan.Zero), "a/b"));
}
