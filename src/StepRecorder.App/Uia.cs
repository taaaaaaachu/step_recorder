using System.Windows.Automation;
using StepRecorder.Core;
using static StepRecorder.App.Native;

namespace StepRecorder.App;

/// <summary>UI Automation で部品情報を取る。対象アプリが応答しないこともあるので、すべて時間制限付きで失敗は null。</summary>
static class Uia
{
    const int TimeoutMs = 300;

    static T? Try<T>(Func<T?> f) where T : class
    {
        var t = Task.Run(() => { try { return f(); } catch { return null; } });
        return t.Wait(TimeoutMs) ? t.Result : null;
    }

    public static AutomationElement? FromPoint(int x, int y) => Try(() => AutomationElement.FromPoint(new(x, y)));

    public static AutomationElement? Focused() => Try(() => AutomationElement.FocusedElement);

    /// <summary>部品情報。矩形は origin を左上とする画像内の座標。部品が大きすぎる(ウィンドウ全体に近い)場合は矩形を付けない。</summary>
    public static ElementInfo? Info(AutomationElement? e, RECT origin) => e == null ? null : Try(() =>
    {
        var c = e.Current;
        var b = c.BoundingRectangle;
        PixelRect? rect = null;
        if (!b.IsEmpty && b.Width * b.Height < origin.Width * origin.Height * 0.5)
            rect = new((int)b.X - origin.Left, (int)b.Y - origin.Top, (int)b.Width, (int)b.Height);
        return new ElementInfo(NameOf(e), c.ControlType.ProgrammaticName.Replace("ControlType.", ""), c.AutomationId, rect);
    });

    /// <summary>入力欄の現在値。パスワード欄は伏せ字。値を持たない部品は null。</summary>
    public static string? Value(AutomationElement e) => Try(() =>
    {
        if (e.Current.IsPassword) return "●●●●";
        return e.TryGetCurrentPattern(ValuePattern.Pattern, out var p) ? ((ValuePattern)p).Current.Value : null;
    });

    /// <summary>名前。無ければラベル(LabeledBy)の名前。</summary>
    static string NameOf(AutomationElement e)
    {
        var name = e.Current.Name;
        if (!string.IsNullOrWhiteSpace(name)) return name;
        return e.Current.LabeledBy?.Current.Name ?? "";
    }
}
