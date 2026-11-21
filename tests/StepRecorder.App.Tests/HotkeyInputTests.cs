using System.Windows.Input;
using StepRecorder.Core;

namespace StepRecorder.App.Tests;

public class HotkeyInputTests
{
    [Fact]
    public void 修飾キーと組み合わせたときだけ確定する()
    {
        Assert.Equal("Ctrl+Shift+F9", HotkeyInput.FromKey(ModifierKeys.Control | ModifierKeys.Shift, Key.F9));
        Assert.Equal("Ctrl+Alt+K", HotkeyInput.FromKey(ModifierKeys.Alt | ModifierKeys.Control, Key.K));
        Assert.Null(HotkeyInput.FromKey(ModifierKeys.None, Key.F9)); // 修飾キーなし
        Assert.Null(HotkeyInput.FromKey(ModifierKeys.Control, Key.LeftCtrl)); // 修飾キーだけ
    }

    [Fact]
    public void 入力待ちの表示()
    {
        Assert.Equal(HotkeyInput.Prompt, HotkeyInput.Pending(ModifierKeys.None));
        Assert.Equal("Ctrl+Shift+…", HotkeyInput.Pending(ModifierKeys.Control | ModifierKeys.Shift));
    }

    [Fact]
    public void 設定文字列から仮想キーコードへ()
    {
        Assert.True(HotkeyInput.TryParse("Ctrl+Shift+F9", out var mods, out var vk));
        Assert.Equal((HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 0x78), (mods, vk));
        // 欄で数字キーを押すと WPF の名前(D1)で入る。それも解釈できる
        Assert.True(HotkeyInput.TryParse(HotkeyInput.FromKey(ModifierKeys.Control, Key.D1)!, out _, out vk));
        Assert.Equal(0x31, vk);
        Assert.False(HotkeyInput.TryParse("Ctrl+NoSuchKey", out _, out _));
    }
}
