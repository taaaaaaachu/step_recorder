using System.Runtime.InteropServices;
using StepRecorder.Core;
using static StepRecorder.App.Native;

namespace StepRecorder.App;

/// <summary>つながっているモニター。</summary>
static class Monitors
{
    public static IReadOnlyList<DisplayMonitor> All()
    {
        var handles = new List<nint>();
        EnumDisplayMonitors(0, 0, (nint monitor, nint _, ref RECT _, nint _) =>
        {
            handles.Add(monitor);
            return true;
        }, 0);

        var found = new List<(nint, PixelRect, bool)>();
        foreach (var handle in handles)
            if (Info(handle) is { } info) found.Add((handle, info.rcMonitor.ToPixelRect(), (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
        return DisplayMonitor.Numbered(found);
    }

    /// <summary>外されていれば null。</summary>
    public static MONITORINFO? Info(nint monitor)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(monitor, ref info) ? info : null;
    }

    /// <summary>拡大率(%)。0 は不明。</summary>
    public static int ScalePercent(nint monitor) =>
        GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? (int)Math.Round(dpi * 100 / 96.0) : 0;
}
