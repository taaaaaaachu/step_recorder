using System.Windows.Input;
using StepRecorder.Core;

namespace StepRecorder.App;

/// <summary>ホットキーの設定文字列("Ctrl+Shift+F9")と WPF のキー・仮想キーコードの変換。</summary>
static class HotkeyInput
{
    public const string Prompt = "キーを押してください";

    /// <summary>押されたキーから設定文字列を作る。修飾キーだけ、または修飾キーなしなら null(まだ確定しない)。</summary>
    public static string? FromKey(ModifierKeys modifiers, Key key) =>
        modifiers == ModifierKeys.None || IsModifier(key) ? null : new Hotkey((HotkeyModifiers)modifiers, key.ToString()).ToString();

    /// <summary>入力待ちの表示。修飾キーを押している間は "Ctrl+Shift+…" のように見せる。</summary>
    public static string Pending(ModifierKeys modifiers) =>
        modifiers == ModifierKeys.None ? Prompt : $"{new Hotkey((HotkeyModifiers)modifiers, "")}…";

    /// <summary>設定文字列を RegisterHotKey 用の修飾キーと仮想キーコードにする。</summary>
    public static bool TryParse(string text, out HotkeyModifiers modifiers, out int vk)
    {
        (modifiers, vk) = (HotkeyModifiers.None, 0);
        if (!Hotkey.TryParse(text, out var hk) || !Enum.TryParse<Key>(hk.Key, true, out var key)) return false;
        (modifiers, vk) = (hk.Modifiers, KeyInterop.VirtualKeyFromKey(key));
        return vk != 0;
    }

    static bool IsModifier(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
}
