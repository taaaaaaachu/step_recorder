namespace StepRecorder.Core;

/// <summary>
/// 押したキー 1 つ。Time は動画上の時刻(その動画ファイルの時刻)。
/// IsChar は文字を打ったもの(続けて打つと 1 つの表示にまとめる)。それ以外は Ctrl+S・Enter・← など。
/// </summary>
public sealed record KeyStroke(TimeSpan Time, string Text, bool IsChar);

/// <summary>画面に出すキーの表示 1 つ。Start から End まで出す。</summary>
public sealed record KeyChip(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// 動画に重ねるキーの表示の時間割。焼き込み・編集画面・HTML はどれもこれを描くだけにして、見た目を揃える。
/// </summary>
public static class KeyOverlay
{
    /// <summary>最後に押してから表示を消すまで。</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(1.5);
    /// <summary>これより間が空いたら、文字を新しい表示に分ける。</summary>
    public static readonly TimeSpan RunGap = TimeSpan.FromSeconds(1);
    /// <summary>同じキー(← など)をこの間隔で押し続けたら「← ×3」にまとめる。</summary>
    public static readonly TimeSpan RepeatGap = TimeSpan.FromSeconds(0.6);
    /// <summary>同時に出す表示の数(新しいものから)。</summary>
    public const int MaxVisible = 3;
    /// <summary>続けて打った文字は、末尾のこの文字数だけ出す。</summary>
    public const int MaxRunLength = 24;

    // 見た目。文字の大きさに対する比率で決める(焼き込み・編集画面・HTML の CSS で同じ値を使う)
    public const double PadX = 0.45, PadY = 0.15, Gap = 0.3, Radius = 0.3, Bottom = 0.8;

    /// <summary>高さ height(画素)の動画に出す文字の大きさ(画素)。</summary>
    public static double FontSize(double height) => Math.Clamp(height / 18, 12, 44);

    /// <summary>キーを出さない設定かどうかに関わらず、そのステップで記録したキー(Recorded)。</summary>
    public static IReadOnlyList<KeyStroke> StrokesFor(Session session, Step step) => step.HideKeys ? [] : Recorded(session, step);

    /// <summary>
    /// 動画のステップで押したキー(そのステップの動画ファイルの時刻)。動画のステップでなければ空。
    /// 全編から切り出した区間は、全編のキーのうち区間の中のもの。
    /// </summary>
    public static IReadOnlyList<KeyStroke> Recorded(Session session, Step step) => InStep(session, step, v => v.Keys, step.Keys, k => k.Time);

    /// <summary>動画のステップの分の記録。録画ボタンの動画はそのステップの own、全編から切り出した区間はその全編の whole の、どちらも区間の中のもの。</summary>
    internal static IReadOnlyList<T> InStep<T>(Session session, Step step, Func<FullVideo, List<T>?> whole, List<T>? own, Func<T, TimeSpan> time)
    {
        if (step.VideoFile == null) return [];
        var (start, end) = (step.VideoStart ?? TimeSpan.Zero, step.VideoEnd ?? TimeSpan.MaxValue);
        var list = session.FullVideo(step.VideoFile) is { } full ? whole(full) : own;
        return (list ?? []).Where(x => time(x) >= start && time(x) < end).ToList();
    }

    public static IReadOnlyList<KeyChip> Chips(IReadOnlyList<KeyStroke> strokes)
    {
        var chips = new List<(TimeSpan Start, TimeSpan Last, string Text, bool IsChar, int Count)>();
        foreach (var k in strokes.OrderBy(k => k.Time))
        {
            if (chips.Count > 0 && chips[^1] is var last)
            {
                var gap = k.Time - last.Last;
                if (k.IsChar && last.IsChar && gap <= RunGap)
                {
                    chips[^1] = last with { Last = k.Time, Text = last.Text + k.Text };
                    continue;
                }
                if (!k.IsChar && !last.IsChar && k.Text == last.Text && gap <= RepeatGap)
                {
                    chips[^1] = last with { Last = k.Time, Count = last.Count + 1 };
                    continue;
                }
            }
            chips.Add((k.Time, k.Time, k.Text, k.IsChar, 1));
        }
        return chips.Select(c => new KeyChip(c.Start, c.Last + Linger, Label(c.Text, c.IsChar, c.Count))).ToList();
    }

    static string Label(string text, bool isChar, int count)
    {
        if (!isChar) return count > 1 ? $"{text} ×{count}" : text;
        if (string.IsNullOrWhiteSpace(text)) return "Space";
        return text.Length > MaxRunLength ? "…" + text[^MaxRunLength..] : text;
    }

    /// <summary>time に出す表示(古い順。最大 MaxVisible 個)。</summary>
    public static IReadOnlyList<string> Visible(IReadOnlyList<KeyChip> chips, TimeSpan time)
    {
        var shown = chips.Where(c => c.Start <= time && time < c.End).Select(c => c.Text).ToList();
        return shown.Count > MaxVisible ? shown[^MaxVisible..] : shown;
    }

    /// <summary>
    /// 合成先(上の行から並んだ BGRA、w×h)の (x, y) に、乗算済みアルファの BGRA(sw×sh)を重ねる。はみ出した分は捨てる。
    /// </summary>
    public static void Blend(byte[] dst, int w, int h, byte[] src, int sw, int sh, int x, int y)
    {
        for (var row = Math.Max(0, -y); row < sh && y + row < h; row++)
            for (var col = Math.Max(0, -x); col < sw && x + col < w; col++)
            {
                var s = (row * sw + col) * 4;
                var a = src[s + 3];
                if (a == 0) continue;
                var d = ((y + row) * w + x + col) * 4;
                for (var c = 0; c < 3; c++) dst[d + c] = (byte)(src[s + c] + (dst[d + c] * (255 - a) + 127) / 255);
                dst[d + 3] = 255;
            }
    }
}
