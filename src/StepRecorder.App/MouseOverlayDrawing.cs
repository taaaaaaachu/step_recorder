using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StepRecorder.Core;

namespace StepRecorder.App;

/// <summary>
/// マウスの表示の描き方。焼き込み(MouseBurner)と編集画面(MouseOverlayView)で同じものを使う(HTML は同じ形をスクリプトで描く)。
/// クリックはボタンの色(左が赤・右が青・中が緑・戻る/進むが紫)の輪が広がりながら消え、その右下に押したボタンを塗ったマウスの絵を出す
/// (戻る・進むは横のボタンで絵では塗れないので、胴に向きの矢印を描く)。
/// ホイールは暗い丸に回した向きの白い三角。
/// </summary>
static class MouseOverlayDrawing
{
    // 左・右・中・戻る・進む。HTML のスクリプトも同じ色
    static readonly Color[] Colors = [Color.FromRgb(244, 67, 54), Color.FromRgb(33, 150, 243), Color.FromRgb(76, 175, 80), Color.FromRgb(156, 39, 176), Color.FromRgb(156, 39, 176)];
    static readonly Brush Back = Freeze(new SolidColorBrush(Color.FromArgb(0xD9, 0x20, 0x20, 0x20)));
    static readonly Brush Outline = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)));

    /// <summary>表示 1 つを動画上の位置に描く。area は見えている範囲(マウスの絵がはみ出さないよう置く向きを決める)。</summary>
    public static void Draw(DrawingContext dc, MouseShown s, double r, Rect area)
    {
        var center = new Point(s.Mark.X, s.Mark.Y);
        if (s.Mark.IsWheel) { Wheel(dc, center, r, s.Mark.Action); return; }
        if (s.Phase is { } phase) Ring(dc, center, r, s.Mark.Action, phase);
        Mouse(dc, new Point(s.PicX, s.PicY), r, s.Mark.Action, MouseOverlay.PicSide(s.PicX - area.X, s.PicY - area.Y, r, area.Width, area.Height));
    }

    /// <summary>center に、クリックの輪を描く。phase は広がりの段階(MouseOverlay.Visible)。</summary>
    public static void Ring(DrawingContext dc, Point center, double r, MouseAction action, int phase)
    {
        var p = (phase + 0.5) / MouseOverlay.Phases;
        var fade = 1 - p * 0.8;
        var color = Colors[(int)action];
        var fill = new SolidColorBrush(Color.FromArgb((byte)(255 * 0.35 * fade), color.R, color.G, color.B));
        var ring = new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * fade), color.R, color.G, color.B)), r * 0.14);
        var radius = r * (0.35 + 0.65 * p);
        dc.DrawEllipse(fill, ring, center, radius, radius);
    }

    /// <summary>center に、ホイールの向きの三角を描く。</summary>
    public static void Wheel(DrawingContext dc, Point center, double r, MouseAction action)
    {
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

    /// <summary>
    /// c の右下(side によっては左・上。MouseOverlay.PicSide)に、白いマウスの絵を描く。押したボタン(左・右は上半分の片側、中はホイール)をボタンの色で塗る。
    /// </summary>
    public static void Mouse(DrawingContext dc, Point c, double r, MouseAction action, (bool Left, bool Up) side)
    {
        var color = new SolidColorBrush(Colors[(int)action]);
        var (w, h) = (r * MouseOverlay.PicWidth, r * MouseOverlay.PicHeight);
        var x = side.Left ? c.X - r * MouseOverlay.PicOffset - w : c.X + r * MouseOverlay.PicOffset;
        var y = side.Up ? c.Y - r * MouseOverlay.PicOffset - h : c.Y + r * MouseOverlay.PicOffset;
        var body = new RectangleGeometry(new Rect(x, y, w, h), w * 0.45, w * 0.45);
        var split = y + h * 0.42; // ボタンと胴の境目
        dc.DrawGeometry(Brushes.White, null, body);
        if (action == MouseAction.Middle)
            dc.DrawRoundedRectangle(color, null, new Rect(x + w * 0.36, y + h * 0.08, w * 0.28, h * 0.28), w * 0.14, w * 0.14);
        else if (action is MouseAction.Left or MouseAction.Right)
        {
            dc.PushClip(body);
            dc.DrawRectangle(color, null, new Rect(action == MouseAction.Left ? x : x + w / 2, y, w / 2, split - y));
            dc.Pop();
        }
        else
        {
            // 胴に、戻るなら ←、進むなら →
            var (cy, half, dir) = (split + (y + h - split) / 2, w * 0.3, action == MouseAction.Back ? -1 : 1);
            var arrow = new Pen(color, Math.Max(1.5, r * 0.13)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var (cx, tip) = (x + w / 2, x + w / 2 + dir * half);
            dc.DrawLine(arrow, new Point(cx - dir * half, cy), new Point(tip, cy));
            dc.DrawLine(arrow, new Point(tip - dir * half * 0.6, cy - half * 0.6), new Point(tip, cy));
            dc.DrawLine(arrow, new Point(tip - dir * half * 0.6, cy + half * 0.6), new Point(tip, cy));
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

    // 形ごとの絵。phase は輪の段階で、-1 はマウスの絵(side はその向き)
    readonly Dictionary<(int R, MouseAction Action, int Phase, (bool, bool) Side), Block> blocks = [];

    public bool IsEmpty => marks.Count == 0;

    /// <summary>
    /// bgra(上の行から、w×h)に、動画上の時刻 time のマウスの表示を重ねる。
    /// 位置は元の動画の位置から crop の左上を引き、scale 倍する(トリミング・縮小したフレームに合わせる)。
    /// </summary>
    public void Apply(byte[] bgra, int w, int h, TimeSpan time, PixelRect? crop = null, double scale = 1)
    {
        var r = (int)Math.Round(MouseOverlay.Radius(h));
        (double, double) Map(int x, int y) => ((x - (crop?.X ?? 0)) * scale, (y - (crop?.Y ?? 0)) * scale);
        void Put((int, MouseAction, int, (bool, bool)) key, (double X, double Y) at)
        {
            if (!blocks.TryGetValue(key, out var block)) blocks[key] = block = Render(key);
            KeyOverlay.Blend(bgra, w, h, block.Pixels, block.Size, block.Size, (int)Math.Round(at.X) - block.Size / 2, (int)Math.Round(at.Y) - block.Size / 2);
        }
        foreach (var s in MouseOverlay.Visible(marks, time))
        {
            var center = Map(s.Mark.X, s.Mark.Y);
            if (s.Phase is { } phase) Put((r, s.Mark.Action, phase, default), center); // ホイールもここ(段階は 0)
            if (s.Mark.IsWheel) continue;
            var pic = Map(s.PicX, s.PicY);
            Put((r, s.Mark.Action, -1, MouseOverlay.PicSide(pic.Item1, pic.Item2, r, w, h)), pic);
        }
    }

    static Block Render((int R, MouseAction Action, int Phase, (bool, bool) Side) key)
    {
        var (r, action, phase, side) = key;
        var size = 2 * ((int)Math.Ceiling(r * (MouseOverlay.PicOffset + MouseOverlay.PicHeight)) + 2); // 押した所を中心に、マウスの絵と線の太さまで入る大きさ
        var (visual, c) = (new DrawingVisual(), new Point(size / 2.0, size / 2.0));
        using (var dc = visual.RenderOpen())
            if (MouseOverlay.IsWheel(action)) MouseOverlayDrawing.Wheel(dc, c, r, action);
            else if (phase < 0) MouseOverlayDrawing.Mouse(dc, c, r, action, side);
            else MouseOverlayDrawing.Ring(dc, c, r, action, phase);
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
    IReadOnlyList<MouseShown> shown = [];

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
    internal IReadOnlyList<MouseShown> Shown => shown;

    protected override void OnRender(DrawingContext dc)
    {
        foreach (var s in shown) MouseOverlayDrawing.Draw(dc, s, Radius, Area);
    }
}
