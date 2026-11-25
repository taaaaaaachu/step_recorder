using System.Runtime.InteropServices;
using System.Text;
using StepRecorder.Core;

namespace StepRecorder.App;

/// <summary>Win32 API の宣言。</summary>
static class Native
{
    public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    public const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104,
        WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_MOUSEWHEEL = 0x020A, WM_XBUTTONDOWN = 0x020B, WM_MOUSEHWHEEL = 0x020E, WM_QUIT = 0x0012, WM_HOTKEY = 0x0312;
    public const int VK_PACKET = 0xE7;
    public const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    public const uint GA_ROOT = 2, PW_RENDERFULLCONTENT = 2, SRCCOPY = 0x00CC0020, WHITENESS = 0x00FF0062, GW_HWNDNEXT = 2, GW_OWNER = 4;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_NOACTIVATE = 0x08000000L, WS_EX_TOOLWINDOW = 0x00000080L, WS_EX_TRANSPARENT = 0x00000020L;
    public const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    public const int SM_CXDOUBLECLK = 36, SM_CYDOUBLECLK = 37;
    public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    public const int WM_IME_CONTROL = 0x0283, IMC_GETOPENSTATUS = 0x0005, IMC_SETOPENSTATUS = 0x0006;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; public readonly int Width => Right - Left; public readonly int Height => Bottom - Top; public readonly PixelRect ToPixelRect() => new(Left, Top, Width, Height); public readonly bool Contains(POINT p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)] public struct CURSORINFO { public int cbSize; public uint flags; public nint hCursor; public POINT ptScreenPos; }
    [StructLayout(LayoutKind.Sequential)] public struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public nint hbmMask, hbmColor; }
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public POINT pt; }

    public delegate nint HookProc(int nCode, nint wParam, nint lParam);
    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    public delegate bool MonitorEnumProc(nint monitor, nint hdc, ref RECT rect, nint lParam);

    [DllImport("user32.dll", SetLastError = true)] public static extern nint SetWindowsHookEx(int idHook, HookProc fn, nint hMod, uint threadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] public static extern nint CallNextHookEx(nint hook, int nCode, nint wParam, nint lParam);
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint tid, uint msg, nint w, nint l);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] public static extern nint GetModuleHandle(string? name);

    [DllImport("user32.dll")] public static extern nint WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] public static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport("user32.dll")] public static extern nint GetTopWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(nint hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(nint hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    public const uint MONITOR_DEFAULTTONULL = 0, MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2, MONITORINFOF_PRIMARY = 1;
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc proc, nint lParam);
    [DllImport("user32.dll")] public static extern nint MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] public static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] public static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(nint hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] public static extern nint SendMessageTimeout(nint hwnd, int msg, nint w, nint l, uint flags, uint timeout, out nint result);
    [DllImport("imm32.dll")] public static extern nint ImmGetDefaultIMEWnd(nint hwnd);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint hwnd, int attr, out int value, int size);

    public const uint CURSOR_SHOWING = 1, DI_NORMAL = 3;
    [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] public static extern bool GetIconInfo(nint icon, out ICONINFO info);
    [DllImport("user32.dll")] public static extern bool DrawIconEx(nint hdc, int x, int y, nint icon, int cx, int cy, uint step, nint brush, uint flags);
    [DllImport("user32.dll")] public static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);
    [DllImport("gdi32.dll")] public static extern uint GetPixel(nint hdc, int x, int y);
    [DllImport("user32.dll")] public static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(nint hwnd, nint hdc);
    [DllImport("gdi32.dll")] public static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] public static extern nint CreateCompatibleBitmap(nint hdc, int w, int h);
    [DllImport("gdi32.dll")] public static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    public const int HALFTONE = 4;
    [DllImport("gdi32.dll")] public static extern bool StretchBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(nint hdc, int mode);
    [DllImport("user32.dll")] static extern nint GetWindowDpiAwarenessContext(nint hwnd);
    [DllImport("user32.dll")] static extern nint SetThreadDpiAwarenessContext(nint context);

    /// <summary>
    /// hwnd が自分で描いている大きさでのウィンドウ矩形。DPI 非対応・システム DPI 対応のアプリは、
    /// 置かれたモニターの拡大率と違う DPI で描き、それを DWM が伸び縮みさせて見せているので、GetWindowRect とは大きさが違う。
    /// </summary>
    public static RECT DrawnWindowRect(nint hwnd)
    {
        var old = SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(hwnd));
        try { return GetWindowRect(hwnd, out var r) ? r : default; }
        finally { if (old != 0) SetThreadDpiAwarenessContext(old); }
    }

    public static bool KeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// window のキーボード配列で vk を押したときに打たれる文字(Shift と CapsLock は効かせる)。文字にならないキーは null。
    /// 押されているキーの状態は変えない(ToUnicodeEx のフラグ 4。デッドキーや IME の入力を乱さない)。
    /// </summary>
    public static char? TypedChar(int vk, bool shift, nint window)
    {
        var state = new byte[256];
        if (shift) state[VK_SHIFT] = 0x80;
        state[VK_CAPITAL] = (byte)(GetKeyState(VK_CAPITAL) & 1);
        var layout = GetKeyboardLayout(GetWindowThreadProcessId(window, out _));
        var text = new StringBuilder(8);
        var n = ToUnicodeEx((uint)vk, MapVirtualKeyEx((uint)vk, 0 /*MAPVK_VK_TO_VSC*/, layout), state, text, text.Capacity, 4, layout);
        return n == 1 ? text[0] : null; // デッドキー(-1)や 2 文字以上は出さない
    }

    const int VK_CAPITAL = 0x14;
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] static extern uint MapVirtualKeyEx(uint code, uint type, nint layout);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, StringBuilder text, int size, uint flags, nint layout);

    /// <summary>画面の取り込み(静止画・動画)に写らないようにする。画面には見えたまま。Windows 10 2004 以降。</summary>
    public static void ExcludeFromCapture(nint hwnd) => SetWindowDisplayAffinity(hwnd, 0x11 /*WDA_EXCLUDEFROMCAPTURE*/);

    public static RECT ToRect(PixelRect r) => new() { Left = r.X, Top = r.Y, Right = r.X + r.Width, Bottom = r.Y + r.Height };

    /// <summary>プロセスの exe のフルパス。管理者権限のプロセスでも取れる。取れなければ null。</summary>
    public static string? ProcessPath(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0) return null;
        try
        {
            var path = new StringBuilder(1024);
            var size = path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : null;
        }
        finally { CloseHandle(process); }
    }

    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    [DllImport("kernel32.dll")] static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref int size);

    public static uint ProcessId(nint hwnd) { GetWindowThreadProcessId(hwnd, out var pid); return pid; }

    public static string Text(nint hwnd) { var sb = new StringBuilder(512); GetWindowText(hwnd, sb, sb.Capacity); return sb.ToString(); }

    public static string ClassName(nint hwnd) { var sb = new StringBuilder(256); GetClassName(hwnd, sb, sb.Capacity); return sb.ToString(); }

    /// <summary>影や見えない枠を除いた、見た目どおりのウィンドウ矩形。</summary>
    public static RECT FrameBounds(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0 ? r
        : GetWindowRect(hwnd, out r) ? r : default;

    public static bool IsCloaked(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int c, sizeof(int)) == 0 && c != 0;

    /// <summary>ウィンドウの IME が開いているか。取れなければ false。</summary>
    public static bool ImeOpen(nint hwnd)
    {
        var ime = ImmGetDefaultIMEWnd(hwnd);
        return ime != 0 && SendMessageTimeout(ime, WM_IME_CONTROL, IMC_GETOPENSTATUS, 0, 2 /*SMTO_ABORTIFHUNG*/, 100, out var open) != 0 && open != 0;
    }

    /// <summary>ウィンドウの IME を開く・閉じる。</summary>
    public static void SetImeOpen(nint hwnd, bool open) =>
        SendMessageTimeout(ImmGetDefaultIMEWnd(hwnd), WM_IME_CONTROL, IMC_SETOPENSTATUS, open ? 1 : 0, 2 /*SMTO_ABORTIFHUNG*/, 100, out _);

    /// <summary>
    /// hwnd を前面に出す。Windows は他のアプリが前面のとき SetForegroundWindow を拒むことがあるので、
    /// 拒まれたら、どのアプリも使わない F24 を一度押したことにしてから(最後の入力をこのプロセスにする)やり直す。
    /// </summary>
    public static void BringToFront(nint hwnd)
    {
        if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd) return;
        INPUT[] tap = [new() { type = 1, ki = new() { wVk = VK_F24 } }, new() { type = 1, ki = new() { wVk = VK_F24, dwFlags = KEYEVENTF_KEYUP } }];
        SendInput((uint)tap.Length, tap, Marshal.SizeOf<INPUT>());
        SetForegroundWindow(hwnd);
    }

    const ushort VK_F24 = 0x87;
    const uint KEYEVENTF_KEYUP = 0x0002;

    // キーボード入力だけを送るので、INPUT の共用体のうち KEYBDINPUT だけを並べる(大きさはマウス入力に合わせる)
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public KEYBDINPUT ki; public long pad; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
}
