using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StepRecorder.Core;

namespace StepRecorder.App;

/// <summary>
/// キーの表示の描き方。焼き込み(KeyBurner)と編集画面(KeyOverlayView)で同じものを使う。
/// 大きさは動画の高さに比例させ(KeyOverlay.FontSize)、下の中央に新しいものほど下に積む。HTML も同じ比率の CSS で描く。
/// </summary>
static class KeyOverlayDrawing
{
    static readonly Brush Back = Freeze(new SolidColorBrush(Color.FromArgb(0xD9, 0x20, 0x20, 0x20)));
    static readonly Typeface Face = new(new FontFamily("Yu Gothic UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    /// <summary>area(動画の画素の大きさ)の下の中央に texts(古い順)を描く。描いた範囲を返す。</summary>
    public static Rect Draw(DrawingContext dc, Size area, IReadOnlyList<string> texts)
    {
        var font = KeyOverlay.FontSize(area.Height);
        var (padX, padY, gap, radius) = (font * KeyOverlay.PadX, font * KeyOverlay.PadY, font * KeyOverlay.Gap, font * KeyOverlay.Radius);
        var bottom = area.Height - font * KeyOverlay.Bottom;
        var drawn = Rect.Empty;
        for (var i = texts.Count - 1; i >= 0; i--)
        {
            var text = new FormattedText(texts[i], CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, font, Brushes.White, 1.0)
            {
                MaxTextWidth = Math.Max(1, area.Width - font * 2 - padX * 2), // 幅に収まらなければ … で切る
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            var size = new Size(text.WidthIncludingTrailingWhitespace + padX * 2, text.Height + padY * 2);
            var box = new Rect((area.Width - size.Width) / 2, bottom - size.Height, size.Width, size.Height);
            dc.DrawRoundedRectangle(Back, null, box, radius, radius);
            dc.DrawText(text, new Point(box.X + padX, box.Y + padY));
            drawn.Union(box);
            bottom = box.Top - gap;
        }
        return drawn;
    }

    static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}

/// <summary>
/// 動画のフレームにキーを焼き込む。表示が変わったときだけ描き、各フレームには画素を重ねるだけにする
/// (フレームごとに描くと 1080p で 1 枚 35ms ほどかかる)。どのスレッドから使ってもよい。
/// </summary>
sealed class KeyBurner(IReadOnlyList<KeyChip> chips)
{
    sealed record Block(byte[] Pixels, int Width, int Height, int X, int Y);

    readonly Dictionary<(int, int, string), Block?> blocks = [];

    public bool IsEmpty => chips.Count == 0;

    /// <summary>bgra(上の行から、w×h)に、動画上の時刻 time のキーを重ねる。</summary>
    public void Apply(byte[] bgra, int w, int h, TimeSpan time)
    {
        var texts = KeyOverlay.Visible(chips, time);
        if (texts.Count == 0) return;
        var key = (w, h, string.Join("\n", texts));
        if (!blocks.TryGetValue(key, out var block)) blocks[key] = block = Render(w, h, texts);
        if (block != null) KeyOverlay.Blend(bgra, w, h, block.Pixels, block.Width, block.Height, block.X, block.Y);
    }

    static Block? Render(int w, int h, IReadOnlyList<string> texts)
    {
        var visual = new DrawingVisual();
        Rect drawn;
        using (var dc = visual.RenderOpen()) drawn = KeyOverlayDrawing.Draw(dc, new Size(w, h), texts);
        if (drawn.IsEmpty) return null;
        var (x, y) = ((int)Math.Floor(drawn.X), (int)Math.Floor(drawn.Y));
        var (bw, bh) = ((int)Math.Ceiling(drawn.Right) - x, (int)Math.Ceiling(drawn.Bottom) - y);
        visual.Offset = new Vector(-x, -y); // 描いた範囲だけを小さな画像にする
        var target = new RenderTargetBitmap(bw, bh, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var pixels = new byte[bw * bh * 4];
        target.CopyPixels(pixels, bw * 4, 0);
        return new Block(pixels, bw, bh, x, y);
    }
}

/// <summary>編集画面で動画に重ねるキーの表示。Time が変わって出すものが変わったときだけ描き直す。</summary>
sealed class KeyOverlayView : FrameworkElement
{
    IReadOnlyList<KeyChip> chips = [];
    IReadOnlyList<string> shown = [];

    public KeyOverlayView() => IsHitTestVisible = false;

    public IReadOnlyList<KeyChip> Chips
    {
        get => chips;
        set
        {
            chips = value;
            shown = [];
            InvalidateVisual();
        }
    }

    public TimeSpan Time
    {
        set
        {
            var now = KeyOverlay.Visible(chips, value);
            if (now.SequenceEqual(shown)) return;
            shown = now;
            InvalidateVisual();
        }
    }

    /// <summary>今出しているもの(テスト用)。</summary>
    internal IReadOnlyList<string> Shown => shown;

    protected override void OnRender(DrawingContext dc)
    {
        if (shown.Count > 0) KeyOverlayDrawing.Draw(dc, RenderSize, shown);
    }
}
