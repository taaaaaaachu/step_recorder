namespace StepRecorder.Core;

public enum KeyKind { Ignore, Typing, Shortcut }

/// <summary>キーボードフックで受けた仮想キーを、記録上の意味に分類する。</summary>
public static class KeyClassifier
{
    // 仮想キーコード (WinUser.h)
    const int VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12,
        VK_ESCAPE = 0x1B, VK_DELETE = 0x2E, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_F1 = 0x70, VK_F24 = 0x87,
        VK_LSHIFT = 0xA0, VK_RMENU = 0xA5;

    public static (KeyKind Kind, string Name) Classify(int vk, bool ctrl, bool alt, bool shift, bool win, bool imeOpen)
    {
        if (vk is VK_SHIFT or VK_CONTROL or VK_MENU or VK_LWIN or VK_RWIN or (>= VK_LSHIFT and <= VK_RMENU))
            return (KeyKind.Ignore, "");

        var name = KeyName(vk);
        if (ctrl || alt || win)
            return name == null ? (KeyKind.Ignore, "") : (KeyKind.Shortcut, Combine(name, ctrl, alt, shift, win));

        if (vk is >= VK_F1 and <= VK_F24)
            return (KeyKind.Shortcut, Combine(name!, false, false, shift, false));

        // ponytail: IME が開いていると Enter/Esc は変換の確定・取消とみなす。IME を開いたまま送信した Enter は記録されない
        if (vk is VK_RETURN or VK_ESCAPE)
            return imeOpen ? (KeyKind.Typing, "") : (KeyKind.Shortcut, Combine(name!, false, false, shift, false));
        if (vk == VK_TAB)
            return (KeyKind.Shortcut, Combine(name!, false, false, shift, false));

        // カーソル移動だけのキーは値を変えないので入力として扱わない
        if (vk is >= 0x21 and <= 0x28 or 0x2D) return (KeyKind.Ignore, "");

        return (KeyKind.Typing, "");
    }

    static string Combine(string name, bool ctrl, bool alt, bool shift, bool win) =>
        string.Concat(ctrl ? "Ctrl+" : "", alt ? "Alt+" : "", shift ? "Shift+" : "", win ? "Win+" : "", name);

    /// <summary>ショートカットとして表記できるキーの名前。表記しないキーは null。</summary>
    static string? KeyName(int vk) => vk switch
    {
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= VK_F1 and <= VK_F24 => $"F{vk - VK_F1 + 1}",
        VK_RETURN => "Enter",
        VK_TAB => "Tab",
        VK_ESCAPE => "Esc",
        VK_DELETE => "Delete",
        VK_BACK => "Backspace",
        0x20 => "Space",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "←",
        0x26 => "↑",
        0x27 => "→",
        0x28 => "↓",
        0x2D => "Insert",
        _ => null,
    };
}

/// <summary>
/// IME のオン・オフを切り替えるキー。キーボードの LANG1/LANG2 は、レイアウトによって
/// VK_KANA(0x15)/VK_HANJA(0x19) か VK_IME_ON(0x16)/VK_IME_OFF(0x1A) で届く。
/// </summary>
public static class ImeKeys
{
    /// <summary>IME をオンにするキーなら true、オフにするキーなら false、それ以外は null。</summary>
    public static bool? OpenState(int vk) => vk switch
    {
        0x15 or 0x16 => true,
        0x19 or 0x1A => false,
        _ => null,
    };
}

public sealed record ClickEvent(DateTimeOffset Time, PixelPoint ScreenPoint, MouseButton Button);

public static class ClickRules
{
    /// <summary>低レベルフックはダブルクリックを2回の押下として通知するので、間隔と距離で判定する。</summary>
    public static bool IsDoubleClick(ClickEvent? prev, ClickEvent cur, int intervalMs, int maxDx, int maxDy) =>
        prev != null && prev.Button == cur.Button
        && (cur.Time - prev.Time).TotalMilliseconds <= intervalMs
        && Math.Abs(cur.ScreenPoint.X - prev.ScreenPoint.X) <= maxDx
        && Math.Abs(cur.ScreenPoint.Y - prev.ScreenPoint.Y) <= maxDy;
}

[Flags]
public enum HotkeyModifiers { None = 0, Alt = 1, Ctrl = 2, Shift = 4, Win = 8 } // RegisterHotKey の MOD_* と同じ値

public sealed record Hotkey(HotkeyModifiers Modifiers, string Key)
{
    /// <summary>"Ctrl+Shift+F9" 形式を解析する。修飾キーなし・キーなしは不正。</summary>
    public static bool TryParse(string text, out Hotkey hotkey)
    {
        hotkey = new(HotkeyModifiers.None, "");
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        var mods = HotkeyModifiers.None;
        foreach (var p in parts[..^1])
        {
            var m = p.ToLowerInvariant() switch
            {
                "ctrl" or "control" => HotkeyModifiers.Ctrl,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "win" => HotkeyModifiers.Win,
                _ => HotkeyModifiers.None,
            };
            if (m == HotkeyModifiers.None) return false;
            mods |= m;
        }
        hotkey = new(mods, parts[^1]);
        return true;
    }

    public override string ToString() =>
        string.Concat(Modifiers.HasFlag(HotkeyModifiers.Ctrl) ? "Ctrl+" : "", Modifiers.HasFlag(HotkeyModifiers.Alt) ? "Alt+" : "",
            Modifiers.HasFlag(HotkeyModifiers.Shift) ? "Shift+" : "", Modifiers.HasFlag(HotkeyModifiers.Win) ? "Win+" : "", Key);
}

/// <summary>画面内の表示の切り替わり(UI Automation の構造変化)をいつ撮るかの判定。</summary>
public static class ContentChangeRules
{
    /// <summary>
    /// 構造変化は短時間に何十回も届くので、quietMs の間届かなくなったら撮る。
    /// 届き続ける(アニメーションなど)場合も、最初から maxWaitMs 経ったら撮る。
    /// </summary>
    public static bool ShouldCapture(DateTimeOffset first, DateTimeOffset last, DateTimeOffset now, int quietMs, int maxWaitMs) =>
        (now - last).TotalMilliseconds >= quietMs || (now - first).TotalMilliseconds >= maxWaitMs;

    /// <summary>同じ大きさの画像(1画素 bytesPerPixel バイト)で、違う画素の割合。大きさが違えば 1。</summary>
    public static double ChangedRatio(byte[] a, byte[] b, int bytesPerPixel)
    {
        if (a.Length != b.Length || a.Length == 0) return 1;
        var changed = 0;
        for (var i = 0; i < a.Length; i += bytesPerPixel)
            if (!a.AsSpan(i, bytesPerPixel).SequenceEqual(b.AsSpan(i, bytesPerPixel))) changed++;
        return (double)changed / (a.Length / bytesPerPixel);
    }
}
