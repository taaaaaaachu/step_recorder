using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using StepRecorder.Core;

namespace StepRecorder.App;

/// <summary>
/// ウィンドウの位置と大きさを覚えて、次に開くときに戻す。座標は WINDOWPLACEMENT のもの(物理ピクセル、メインモニターの作業領域が原点)。
/// 覚えた位置がどのモニターにも入らなければメインモニターの真ん中に、はみ出す大きさなら収めて置く(Geometry.RestoreIn)。
/// </summary>
static class SavedPlacement
{
    /// <summary>今の位置と大きさ(最大化する前)。ウィンドウがあるうち(閉じる前)に呼ぶ。取れなければ null。</summary>
    public static int[]? Capture(Window window)
    {
        var wp = NewPlacement();
        if (!Native.GetWindowPlacement(Handle(window), ref wp)) return null;
        var r = wp.rcNormalPosition;
        return [r.Left, r.Top, r.Right, r.Bottom];
    }

    /// <summary>
    /// 覚えた位置に置く。見せる前(OnSourceInitialized)に呼ぶ。keepSize なら大きさは今のまま(中身で高さが決まるウィンドウ)。
    /// 覚えていなければ何もせず false。
    /// </summary>
    public static bool Restore(Window window, int[]? saved, bool keepSize = false)
    {
        if (saved is not [var left, var top, var right, var bottom]
            || Monitors.Info(Native.MonitorFromPoint(default, Native.MONITOR_DEFAULTTOPRIMARY)) is not { } primary) return false;
        var hwnd = Handle(window);
        if (keepSize && Native.GetWindowRect(hwnd, out var now)) (right, bottom) = (left + now.Width, top + now.Height);
        // WINDOWPLACEMENT の座標を画面の座標に直して、どのモニターに入るかを見る
        var (dx, dy) = (primary.rcWork.Left - primary.rcMonitor.Left, primary.rcWork.Top - primary.rcMonitor.Top);
        var rect = new Native.RECT { Left = left + dx, Top = top + dy, Right = right + dx, Bottom = bottom + dy };
        var work = Monitors.Info(Native.MonitorFromRect(ref rect, Native.MONITOR_DEFAULTTONULL))?.rcWork.ToPixelRect();
        var to = Geometry.RestoreIn(rect.ToPixelRect(), work, primary.rcWork.ToPixelRect());
        if (keepSize)
        {
            // 大きさを指定すると、中身に合わせた高さが固定されてしまうので、位置だけ動かす
            Native.SetWindowPos(hwnd, 0, to.X, to.Y, 0, 0, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOSIZE);
            return true;
        }
        var wp = NewPlacement();
        Native.GetWindowPlacement(hwnd, ref wp);
        wp.showCmd = 0; // SW_HIDE: まだ見せない(最大化は WindowState で WPF がする)
        wp.rcNormalPosition = new() { Left = to.X - dx, Top = to.Y - dy, Right = to.X + to.Width - dx, Bottom = to.Y + to.Height - dy };
        Native.SetWindowPlacement(hwnd, ref wp);
        return true;
    }

    static Native.WINDOWPLACEMENT NewPlacement() => new() { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };

    static nint Handle(Window window) => new WindowInteropHelper(window).Handle;
}
