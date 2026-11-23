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

    /// <summary>幅 width のシークバーで、割合 ratio(0〜1)の位置の X。inset は両端の、つまみの中心が届かない幅。</summary>
    public static double TrackX(double ratio, double width, double inset) => inset + ratio * (width - 2 * inset);

    /// <summary>シークバーの幅を変えても、見えている範囲の at の位置にある時刻が動かないスクロール位置。</summary>
    public static double ZoomScroll(double offset, double at, double oldWidth, double newWidth, double inset)
    {
        var ratio = (offset + at - inset) / Math.Max(1, oldWidth - 2 * inset);
        return Math.Max(0, TrackX(ratio, newWidth, inset) - at);
    }

    /// <summary>x が見えるスクロール位置。見えていれば動かさず、外なら左から 1/4 の所に来るようにする。</summary>
    public static double ScrollToShow(double offset, double viewport, double x) =>
        x >= offset && x <= offset + viewport ? offset : Math.Max(0, x - viewport / 4);

    /// <summary>1 秒 fps コマの動画で、seconds に映っているコマから dir コマ動いたコマの頭の時刻(0〜max に収める)。</summary>
    public static double StepFrame(double seconds, int dir, int fps, double max) =>
        Math.Clamp((Math.Floor(seconds * fps + 1e-6) + dir) / fps, 0, max);

    static int Clamp(double v, int max) => (int)Math.Clamp(Math.Round(v), 0, max);
}
