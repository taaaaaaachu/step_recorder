namespace StepRecorder.Core;

/// <summary>編集画面での矩形操作。結果はいつも画像(w×h)の内側に収める。</summary>
public static class Geometry
{
    public const int MinSize = 4;

    /// <summary>ドラッグの始点と終点から矩形を作る。</summary>
    public static PixelRect FromPoints(double x1, double y1, double x2, double y2, int w, int h)
    {
        int l = Clamp(Math.Min(x1, x2), w), t = Clamp(Math.Min(y1, y2), h);
        int r = Clamp(Math.Max(x1, x2), w), b = Clamp(Math.Max(y1, y2), h);
        return new(l, t, r - l, b - t);
    }

    /// <summary>大きさを保ったまま動かす。画像の外には出さない。</summary>
    public static PixelRect Move(PixelRect r, double dx, double dy, int w, int h) =>
        r with
        {
            X = (int)Math.Clamp(r.X + Math.Round(dx), 0, Math.Max(0, w - r.Width)),
            Y = (int)Math.Clamp(r.Y + Math.Round(dy), 0, Math.Max(0, h - r.Height)),
        };

    /// <summary>右下の角を (x, y) へ動かす。最小サイズは MinSize。</summary>
    public static PixelRect ResizeBottomRight(PixelRect r, double x, double y, int w, int h) =>
        r with
        {
            Width = Math.Max(MinSize, Clamp(x, w) - r.X),
            Height = Math.Max(MinSize, Clamp(y, h) - r.Y),
        };

    public static bool Contains(PixelRect r, double x, double y) =>
        x >= r.X && x <= r.X + r.Width && y >= r.Y && y <= r.Y + r.Height;

    public static bool NearBottomRight(PixelRect r, double x, double y, double tolerance) =>
        Math.Abs(x - (r.X + r.Width)) <= tolerance && Math.Abs(y - (r.Y + r.Height)) <= tolerance;

    public static bool IsUsable(PixelRect r) => r.Width >= MinSize && r.Height >= MinSize;

    /// <summary>
    /// アプリ全体の画像(スクリーン上の all)のうち、操作したウィンドウ(focus)を切り出すトリミング範囲。
    /// 全体と同じなら null(トリミング不要)。
    /// </summary>
    public static PixelRect? CropFor(PixelRect all, PixelRect focus)
    {
        var l = Math.Max(all.X, focus.X);
        var t = Math.Max(all.Y, focus.Y);
        var r = Math.Min(all.X + all.Width, focus.X + focus.Width);
        var b = Math.Min(all.Y + all.Height, focus.Y + focus.Height);
        var crop = new PixelRect(l - all.X, t - all.Y, r - l, b - t);
        return crop == new PixelRect(0, 0, all.Width, all.Height) || !IsUsable(crop) ? null : crop;
    }

    static int Clamp(double v, int max) => (int)Math.Clamp(Math.Round(v), 0, max);
}
