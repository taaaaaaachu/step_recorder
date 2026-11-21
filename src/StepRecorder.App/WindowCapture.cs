using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static StepRecorder.App.Native;

namespace StepRecorder.App;

/// <summary>撮影結果。Bounds は画像の左上に当たるスクリーン座標を含む矩形。</summary>
sealed record Shot(BitmapSource Image, RECT Bounds)
{
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
/// 対象プロセスのウィンドウだけを撮る。PrintWindow でウィンドウごとに描かせるので、
/// 他のアプリや常駐バーが上に重なっていても写り込まない。
/// 同じプロセスのメニュー・ドロップダウン等(別の最上位ウィンドウ)も重ねて合成する。
/// </summary>
static class WindowCapture
{
    public static Shot? Capture(nint root)
    {
        if (root == 0 || !IsWindow(root) || IsIconic(root)) return null;
        var pid = ProcessId(root);
        var frame = FrameBounds(root);
        if (frame.Width <= 0 || frame.Height <= 0) return null;

        // Z オーダーで root より手前にある、同じプロセスのポップアップ
        var popups = new List<(nint Hwnd, RECT Rect)>();
        for (var h = GetTopWindow(0); h != 0 && h != root; h = GetWindow(h, GW_HWNDNEXT))
        {
            if (!IsWindowVisible(h) || IsCloaked(h) || ProcessId(h) != pid || ClassName(h) == "SysShadow") continue;
            var r = FrameBounds(h);
            if (r.Width > 0 && r.Height > 0 && Intersects(r, frame)) popups.Add((h, r));
        }
        popups.Reverse(); // 奥から順に描く

        var u = frame;
        foreach (var (_, r) in popups) u = Union(u, r);

        var screen = GetDC(0);
        var dc = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, u.Width, u.Height);
        var old = SelectObject(dc, bmp);
        try
        {
            Draw(root, frame, u, dc, screen);
            foreach (var (h, r) in popups) Draw(h, r, u, dc, screen);
            SelectObject(dc, old);
            var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            // PrintWindow はアルファを 0 のままにすることがあるので、透過なしの形式に揃える
            var img = new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0);
            img.Freeze();
            return new Shot(img, u);
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
