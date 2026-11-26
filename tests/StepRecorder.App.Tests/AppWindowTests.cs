using System.Runtime.InteropServices;
using System.Windows.Media;

namespace StepRecorder.App.Tests;

/// <summary>対象の一覧に出すウィンドウか。cloak された窓(TextInputHost など)とツールウィンドウ(オーバーレイなど)は出さない。</summary>
public sealed class AppWindowTests
{
    [DesktopFact]
    public void 見えている普通のウィンドウだけがアプリのウィンドウ()
    {
        using var w = new ColorWindow(Colors.Teal);
        var h = w.Handle;
        Assert.True(AppProcesses.IsAppWindow(h));
        Assert.False(AppProcesses.IsAppWindow(0));

        var style = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
        SetWindowLongPtr(h, Native.GWL_EXSTYLE, style | (nint)Native.WS_EX_TOOLWINDOW);
        Assert.False(AppProcesses.IsAppWindow(h));
        SetWindowLongPtr(h, Native.GWL_EXSTYLE, style);
        Assert.True(AppProcesses.IsAppWindow(h));

        var on = 1;
        Assert.Equal(0, DwmSetWindowAttribute(h, 13 /*DWMWA_CLOAK*/, ref on, sizeof(int)));
        Assert.False(AppProcesses.IsAppWindow(h));
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
