using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StepRecorder.Core;
using Geometry = StepRecorder.Core.Geometry;
using static StepRecorder.App.Native;

namespace StepRecorder.App;

/// <summary>
/// 撮影結果。Image はアプリ全体(対象プロセスの見えているウィンドウをすべて重ねたもの)で、
/// Bounds はその画像に当たるスクリーン上の矩形。Focus は操作したウィンドウのスクリーン上の矩形。
/// </summary>
sealed record Shot(BitmapSource Image, RECT Bounds, RECT Focus)
{
    /// <summary>操作したウィンドウを切り出すトリミング範囲。アプリ全体と同じなら null。</summary>
    public PixelRect? Crop => Geometry.CropFor(ToPixel(Bounds), ToPixel(Focus));

    static PixelRect ToPixel(RECT r) => new(r.Left, r.Top, r.Width, r.Height);

    public const int BytesPerPixel = 3; // Image は Bgr24

    /// <summary>画素の並び(前回の撮影との比較用)。</summary>
    public byte[] Pixels => pixels ??= CopyPixels();
    byte[]? pixels;

    byte[] CopyPixels()
    {
        var stride = Image.PixelWidth * BytesPerPixel;
        var buf = new byte[stride * Image.PixelHeight];
        Image.CopyPixels(buf, stride, 0);
        return buf;
    }
}

/// <summary>
/// 対象プロセスのウィンドウだけを撮る。PrintWindow でウィンドウごとに描かせて重ねるので、
/// 他のアプリや常駐バーが上に重なっていても写り込まない。
/// </summary>
static class WindowCapture
{
    /// <summary>
    /// focus を含むアプリ全体を撮る。同じプロセスの見えているウィンドウ(メインウィンドウ・ダイアログ・メニューなど)を
    /// Z オーダーの奥から順に重ねる。画面の外に置かれたウィンドウは含めない。
    /// </summary>
    public static Shot? Capture(nint focus)
    {
        if (focus == 0 || !IsWindow(focus) || IsIconic(focus)) return null;
        var pid = ProcessId(focus);
        var frame = FrameBounds(focus);
        if (frame.Width <= 0 || frame.Height <= 0) return null;

        var screenArea = new RECT
        {
            Left = GetSystemMetrics(SM_XVIRTUALSCREEN), Top = GetSystemMetrics(SM_YVIRTUALSCREEN),
            Right = GetSystemMetrics(SM_XVIRTUALSCREEN) + GetSystemMetrics(SM_CXVIRTUALSCREEN),
            Bottom = GetSystemMetrics(SM_YVIRTUALSCREEN) + GetSystemMetrics(SM_CYVIRTUALSCREEN),
        };
        var windows = new List<(nint Hwnd, RECT Rect)>();
        for (var h = GetTopWindow(0); h != 0; h = GetWindow(h, GW_HWNDNEXT))
        {
            if (h != focus && (!IsWindowVisible(h) || IsIconic(h) || IsCloaked(h) || ProcessId(h) != pid || ClassName(h) == "SysShadow")) continue;
            var r = h == focus ? frame : FrameBounds(h);
            if (r.Width > 0 && r.Height > 0 && Intersects(r, screenArea)) windows.Add((h, r));
        }
        windows.Reverse(); // 奥から順に描く

        var u = frame;
        foreach (var (_, r) in windows) u = Union(u, r);

        var screen = GetDC(0);
        var dc = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, u.Width, u.Height);
        var old = SelectObject(dc, bmp);
        try
        {
            BitBlt(dc, 0, 0, u.Width, u.Height, 0, 0, 0, WHITENESS); // ウィンドウ同士の隙間は白にする
            foreach (var (h, r) in windows) Draw(h, r, u, dc, screen);
            SelectObject(dc, old);
            var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            // PrintWindow はアルファを 0 のままにすることがあるので、透過なしの形式に揃える
            var img = new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0);
            img.Freeze();
            return new Shot(img, u, frame);
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(dc);
            ReleaseDC(0, screen);
        }
    }

    /// <summary>hwnd を描き、見えている範囲 visible だけを合成先に写す。</summary>
    static void Draw(nint hwnd, RECT visible, RECT u, nint dst, nint screen)
    {
        if (!GetWindowRect(hwnd, out var wr) || wr.Width <= 0 || wr.Height <= 0) return;
        var dc = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, wr.Width, wr.Height);
        var old = SelectObject(dc, bmp);
        try
        {
            if (!PrintWindow(hwnd, dc, PW_RENDERFULLCONTENT))
                BitBlt(dc, 0, 0, wr.Width, wr.Height, screen, wr.Left, wr.Top, SRCCOPY); // 描けないウィンドウは画面から
            BitBlt(dst, visible.Left - u.Left, visible.Top - u.Top, visible.Width, visible.Height,
                dc, visible.Left - wr.Left, visible.Top - wr.Top, SRCCOPY);
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(bmp);
            DeleteDC(dc);
        }
    }

    static bool Intersects(RECT a, RECT b) => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    static RECT Union(RECT a, RECT b) => new()
    {
        Left = Math.Min(a.Left, b.Left), Top = Math.Min(a.Top, b.Top),
        Right = Math.Max(a.Right, b.Right), Bottom = Math.Max(a.Bottom, b.Bottom),
    };

    public static void SavePng(BitmapSource img, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
