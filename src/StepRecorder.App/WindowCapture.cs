using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StepRecorder.Core;
using Geometry = StepRecorder.Core.Geometry;
using static StepRecorder.App.Native;

namespace StepRecorder.App;

/// <summary>
/// 撮影結果。Image はアプリ全体(対象プロセスの見えているウィンドウをすべて重ねたもの)かモニター全体で、
/// Bounds はその画像に当たるスクリーン上の矩形。Focus は操作したウィンドウのスクリーン上の矩形。
/// </summary>
sealed record Shot(BitmapSource Image, RECT Bounds, RECT Focus)
{
    /// <summary>操作したウィンドウを切り出すトリミング範囲。画像全体と同じか、画像に載っていなければ null。</summary>
    public PixelRect? Crop => Geometry.CropFor(Bounds.ToPixelRect(), Focus.ToPixelRect());
}

/// <summary>
/// 静止画を撮る。
/// アプリが対象なら、そのウィンドウだけを PrintWindow でウィンドウごとに描かせて重ねるので、
/// 他のアプリや常駐バーが上に重なっていても写り込まない。モニターが対象なら、見えているとおりに撮る。
/// </summary>
static class WindowCapture
{
    /// <summary>
    /// focus を含むアプリ全体を撮る。同じプロセスの見えているウィンドウ(メインウィンドウ・ダイアログ・メニューなど)を
    /// Z オーダーの奥から順に重ねる。画面の外に置かれたウィンドウは含めない。
    /// cursor なら、撮った瞬間のマウスカーソルも重ねて描く(PrintWindow ではカーソルは写らないため)。
    /// </summary>
    public static Shot? Capture(nint focus, bool cursor = false)
    {
        if (focus == 0 || !IsWindow(focus) || IsIconic(focus)) return null;
        var frame = FrameBounds(focus);
        if (frame.Width <= 0 || frame.Height <= 0) return null;
        var windows = AppWindows(focus);

        var u = frame;
        foreach (var (_, r) in windows) u = Union(u, r);

        var image = Render(u, cursor, (dc, screen) =>
        {
            BitBlt(dc, 0, 0, u.Width, u.Height, 0, 0, 0, WHITENESS); // ウィンドウ同士の隙間は白にする
            foreach (var (h, r) in windows) Draw(h, r, u, dc, screen);
        });
        return new Shot(image, u, frame);
    }

    /// <summary>
    /// モニター(bounds)に見えているものをそのまま撮る。操作したウィンドウ focus の範囲は Shot.Focus に入れる。
    /// ExcludeFromCapture したウィンドウは写らない。
    /// </summary>
    public static Shot CaptureMonitor(RECT bounds, nint focus, bool cursor = false)
    {
        var image = Render(bounds, cursor, (dc, screen) =>
            BitBlt(dc, 0, 0, bounds.Width, bounds.Height, screen, bounds.Left, bounds.Top, SRCCOPY));
        return new Shot(image, bounds, FrameBounds(focus));
    }

    /// <summary>スクリーン上の u に当たる画像を draw に描かせる。cursor なら最後にカーソルを重ねる。</summary>
    static BitmapSource Render(RECT u, bool cursor, Action<nint, nint> draw)
    {
        var screen = GetDC(0);
        var dc = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, u.Width, u.Height);
        var old = SelectObject(dc, bmp);
        try
        {
            draw(dc, screen);
            if (cursor) DrawCursor(dc, u);
            SelectObject(dc, old);
            var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            // PrintWindow はアルファを 0 のままにすることがあるので、透過なしの形式に揃える
            var img = new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0);
            img.Freeze();
            return img;
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(dc);
            ReleaseDC(0, screen);
        }
    }

    /// <summary>
    /// focus と同じプロセスの見えているウィンドウ(メインウィンドウ・ダイアログ・メニューなど)を、Z オーダーの奥から順に返す。
    /// 画面の外に置かれたウィンドウは含めない。focus は見えていなくても含める。
    /// </summary>
    public static List<(nint Hwnd, RECT Rect)> AppWindows(nint focus)
    {
        var pid = ProcessId(focus);
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
            var r = FrameBounds(h);
            if (r.Width > 0 && r.Height > 0 && Intersects(r, screenArea)) windows.Add((h, r));
        }
        windows.Reverse();
        return windows;
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

    /// <summary>今のマウスカーソルを、画像(スクリーン上の u)の該当位置に描く。カーソルが隠れていれば描かない。</summary>
    static void DrawCursor(nint dc, RECT u)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0 || !GetIconInfo(ci.hCursor, out var ii)) return;
        // GetIconInfo が作ったビットマップは呼んだ側で消す
        if (ii.hbmMask != 0) DeleteObject(ii.hbmMask);
        if (ii.hbmColor != 0) DeleteObject(ii.hbmColor);
        DrawIconEx(dc, ci.ptScreenPos.X - ii.xHotspot - u.Left, ci.ptScreenPos.Y - ii.yHotspot - u.Top, ci.hCursor, 0, 0, 0, 0, DI_NORMAL);
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
