using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StepRecorder.Core;

namespace StepRecorder.App;

/// <summary>
/// マウスの表示の描き方。焼き込み(MouseBurner)と編集画面(MouseOverlayView)で同じものを使う(HTML は同じ形をスクリプトで描く)。
/// クリックはボタンの色(左が赤・右が青・中が緑)の輪が広がりながら消え、その右下に押したボタンを塗ったマウスの絵を出す。
/// ホイールは暗い丸に回した向きの白い三角。
/// </summary>
static class MouseOverlayDrawing
{
    static readonly Color[] Colors = [Color.FromRgb(244, 67, 54), Color.FromRgb(33, 150, 243), Color.FromRgb(76, 175, 80)];
    static readonly Brush Back = Freeze(new SolidColorBrush(Color.FromArgb(0xD9, 0x20, 0x20, 0x20)));
    static readonly Brush Outline = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)));

    /// <summary>
    /// center に、半径 r の表示を描く。phase はクリックの輪の広がりの段階(MouseOverlay.Visible)。
    /// side はマウスの絵を左・上に置くか(MouseOverlay.PicSide)。
    /// </summary>
    public static void Draw(DrawingContext dc, Point center, double r, MouseAction action, int phase, (bool Left, bool Up) side = default)
    {
        if (action < MouseAction.WheelUp)
        {
            var p = (phase + 0.5) / MouseOverlay.Phases;
            var fade = 1 - p * 0.8;
            var color = Colors[(int)action];
            var fill = new SolidColorBrush(Color.FromArgb((byte)(255 * 0.35 * fade), color.R, color.G, color.B));
            var ring = new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * fade), color.R, color.G, color.B)), r * 0.14);
            var radius = r * (0.35 + 0.65 * p);
            dc.DrawEllipse(fill, ring, center, radius, radius);
            DrawMouse(dc, center, r, action, new SolidColorBrush(color), side);
            return;
        }
        dc.DrawEllipse(Back, null, center, r * 0.75, r * 0.75);
        var (dx, dy) = action switch { MouseAction.WheelUp => (0, -1), MouseAction.WheelDown => (0, 1), MouseAction.WheelLeft => (-1, 0), _ => (1, 0) };
        var q = r * 0.4;
        var tip = new Point(center.X + dx * q, center.Y + dy * q);
        var a = new Point(center.X - dx * q * 0.6 - dy * q, center.Y - dy * q * 0.6 + dx * q);
        var b = new Point(center.X - dx * q * 0.6 + dy * q, center.Y - dy * q * 0.6 - dx * q);
        var triangle = new StreamGeometry();
        using (var g = triangle.Open())
        {
            g.BeginFigure(tip, true, true);
            g.PolyLineTo([a, b], false, false);
        }
        dc.DrawGeometry(Brushes.White, null, triangle);
    }

    /// <summary>白いマウスの絵。押したボタン(左・右は上半分の片側、中はホイール)を color で塗る。</summary>
    static void DrawMouse(DrawingContext dc, Point c, double r, MouseAction action, Brush color, (bool Left, bool Up) side)
    {
        var (w, h) = (r * MouseOverlay.PicWidth, r * MouseOverlay.PicHeight);
        var x = side.Left ? c.X - r * MouseOverlay.PicOffset - w : c.X + r * MouseOverlay.PicOffset;
        var y = side.Up ? c.Y - r * MouseOverlay.PicOffset - h : c.Y + r * MouseOverlay.PicOffset;
        var body = new RectangleGeometry(new Rect(x, y, w, h), w * 0.45, w * 0.45);
        var split = y + h * 0.42; // ボタンと胴の境目
        dc.DrawGeometry(Brushes.White, null, body);
        if (action == MouseAction.Middle)
            dc.DrawRoundedRectangle(color, null, new Rect(x + w * 0.36, y + h * 0.08, w * 0.28, h * 0.28), w * 0.14, w * 0.14);
        else
        {
            dc.PushClip(body);
            dc.DrawRectangle(color, null, new Rect(action == MouseAction.Left ? x : x + w / 2, y, w / 2, split - y));
            dc.Pop();
        }
        var pen = new Pen(Outline, Math.Max(1, r * 0.07));
        dc.DrawGeometry(null, pen, body);
        dc.DrawLine(pen, new Point(x, split), new Point(x + w, split));
        if (action != MouseAction.Middle) dc.DrawLine(pen, new Point(x + w / 2, y), new Point(x + w / 2, split));
    }

    static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}

/// <summary>
/// 動画のフレームにマウスの表示を焼き込む。形(操作と段階)ごとに 1 度だけ描き、各フレームには位置をずらして重ねるだけにする。
/// どのスレッドから使ってもよい。
/// </summary>
sealed class MouseBurner(IReadOnlyList<MouseMark> marks)
{
    sealed record Block(byte[] Pixels, int Size);

    readonly Dictionary<(int, MouseAction, int, (bool, bool)), Block> blocks = [];

    public bool IsEmpty => marks.Count == 0;

    /// <summary>
    /// bgra(上の行から、w×h)に、動画上の時刻 time のマウスの表示を重ねる。
    /// 位置は元の動画の位置から crop の左上を引き、scale 倍する(トリミング・縮小したフレームに合わせる)。
    /// </summary>
    public void Apply(byte[] bgra, int w, int h, TimeSpan time, PixelRect? crop = null, double scale = 1)
    {
        foreach (var (m, phase) in MouseOverlay.Visible(marks, time))
        {
            var r = (int)Math.Round(MouseOverlay.Radius(h));
            var (x, y) = ((m.X - (crop?.X ?? 0)) * scale, (m.Y - (crop?.Y ?? 0)) * scale);
            var side = m.IsWheel ? default : MouseOverlay.PicSide(x, y, r, w, h);
            var key = (r, m.Action, phase, side);
            if (!blocks.TryGetValue(key, out var block)) blocks[key] = block = Render(r, m.Action, phase, side);
            KeyOverlay.Blend(bgra, w, h, block.Pixels, block.Size, block.Size, (int)Math.Round(x) - block.Size / 2, (int)Math.Round(y) - block.Size / 2);
        }
    }

    static Block Render(int r, MouseAction action, int phase, (bool, bool) side)
    {
        var size = 2 * ((int)Math.Ceiling(r * (MouseOverlay.PicOffset + MouseOverlay.PicHeight)) + 2); // 押した所を中心に、マウスの絵と線の太さまで入る大きさ
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) MouseOverlayDrawing.Draw(dc, new Point(size / 2.0, size / 2.0), r, action, phase, side);
        var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var pixels = new byte[size * size * 4];
        target.CopyPixels(pixels, size * 4, 0);
        return new Block(pixels, size);
    }
}

/// <summary>編集画面で動画に重ねるマウスの表示。大きさは動画と同じ画素の座標で置く(トリミング前の全体)。</summary>
sealed class MouseOverlayView : FrameworkElement
{
    IReadOnlyList<MouseMark> marks = [];
    IReadOnlyList<(MouseMark, int)> shown = [];

    public MouseOverlayView() => IsHitTestVisible = false;

    public IReadOnlyList<MouseMark> Marks
    {
        get => marks;
        set
        {
            marks = value;
            shown = [];
            InvalidateVisual();
        }
    }

    public TimeSpan Time
    {
        set
        {
            var now = MouseOverlay.Visible(marks, value);
            if (now.SequenceEqual(shown)) return;
            shown = now;
            InvalidateVisual();
        }
    }

    /// <summary>表示の半径(書き出しと同じく、見えている範囲の高さで決まる。MouseOverlay.Radius)。</summary>
    public double Radius { get; set; }

    /// <summary>見えている範囲(トリミングの範囲)。マウスの絵がはみ出さないよう置く向きを決める。</summary>
    public Rect Area { get; set; }

    /// <summary>今出しているもの(テスト用)。</summary>
    internal IReadOnlyList<(MouseMark Mark, int Phase)> Shown => shown;

    protected override void OnRender(DrawingContext dc)
    {
        foreach (var (m, phase) in shown)
            MouseOverlayDrawing.Draw(dc, new Point(m.X, m.Y), Radius, m.Action, phase,
                MouseOverlay.PicSide(m.X - Area.X, m.Y - Area.Y, Radius, Area.Width, Area.Height));
    }
}
