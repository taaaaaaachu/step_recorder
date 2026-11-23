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
        if (vk == VK_TAB || IsSpecial(vk))
            return (KeyKind.Shortcut, Combine(name!, false, false, shift, false));

        // カーソル移動だけのキーは値を変えないので入力として扱わない
        if (vk is >= 0x21 and <= 0x28 or 0x2D) return (KeyKind.Ignore, "");

        return (KeyKind.Typing, "");
    }

    /// <summary>
    /// ブラウザー・音量・メディアなどの専用のキー(VK_BROWSER_BACK〜VK_LAUNCH_APP2)。キーボードの専用キーのほか、
    /// 多ボタンマウスの 6 個目以降のボタンも、付属のソフトがこれらのキーに置き換えて送ることが多い。文字は打たないのでキー操作として扱う。
    /// </summary>
    static bool IsSpecial(int vk) => vk is >= 0xA6 and <= 0xB7;

    internal static string Combine(string name, bool ctrl, bool alt, bool shift, bool win) =>
        string.Concat(ctrl ? "Ctrl+" : "", alt ? "Alt+" : "", shift ? "Shift+" : "", win ? "Win+" : "", name);

    /// <summary>ショートカットとして表記できるキーの名前。表記しないキーは null。</summary>
    internal static string? KeyName(int vk) => vk switch
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
        0x2C => "PrintScreen",
        0x13 => "Pause",
        0x5D => "Apps",
        0xA6 => "ブラウザーの戻る",
        0xA7 => "ブラウザーの進む",
        0xA8 => "ブラウザーの更新",
        0xA9 => "ブラウザーの中止",
        0xAA => "検索",
        0xAB => "お気に入り",
        0xAC => "ブラウザーのホーム",
        0xAD => "ミュート",
        0xAE => "音量 −",
        0xAF => "音量 +",
        0xB0 => "次の曲",
        0xB1 => "前の曲",
        0xB2 => "メディアの停止",
        0xB3 => "再生/一時停止",
        0xB4 => "メール",
        0xB5 => "メディアの選択",
        0xB6 => "アプリ 1",
        0xB7 => "アプリ 2",
        _ => null,
    };
}

/// <summary>動画に重ねるキーの表記。</summary>
public static class KeyLabel
{
    /// <summary>
    /// typed はそのキーで打たれる文字(Shift は効かせ、Ctrl などは効かせない。キーボードの配列で決まるので呼ぶ側で求める)。
    /// 修飾キーと一緒なら「Ctrl+S」、文字なら打った文字そのもの(IsChar)、それ以外は「Enter」「Shift+Tab」など。出さないキーは null。
    /// </summary>
    public static (string Text, bool IsChar)? Of(int vk, bool ctrl, bool alt, bool shift, bool win, char? typed)
    {
        if (vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5)) return null; // 修飾キーだけ
        var name = KeyClassifier.KeyName(vk);
        var printable = typed is { } c && !char.IsControl(c);
        if (ctrl || alt || win)
        {
            name ??= printable ? char.ToUpperInvariant(typed!.Value).ToString() : null;
            return name == null ? null : (KeyClassifier.Combine(name, ctrl, alt, shift, win), false);
        }
        if (printable) return (typed!.Value.ToString(), true);
        return name == null ? null : (KeyClassifier.Combine(name, false, false, shift, false), false);
    }
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
