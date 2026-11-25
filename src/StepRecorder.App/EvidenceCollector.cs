using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using StepRecorder.Core;
using static StepRecorder.App.Native;

namespace StepRecorder.App;

/// <summary>記録開始時に、エビデンスとして載せる環境の情報を集める。取れない項目は空のまま。</summary>
static class EvidenceCollector
{
    static EvidenceInfo Common() => new()
    {
        MachineName = Environment.MachineName,
        Os = RuntimeInformation.OSDescription,
        RecorderVersion = AppVersion.Current,
    };

    /// <summary>モニターが対象なら、アプリの情報は無い。</summary>
    public static EvidenceInfo Collect(DisplayMonitor monitor)
    {
        var e = Common();
        e.Screen = $"{monitor.Bounds.Width}×{monitor.Bounds.Height}";
        e.ScalePercent = Monitors.ScalePercent(monitor.Handle);
        return e;
    }

    public static EvidenceInfo Collect(Process target)
    {
        var e = Common();
        try
        {
            var path = target.MainModule!.FileName;
            var v = FileVersionInfo.GetVersionInfo(path);
            e.AppFile = Path.GetFileName(path);
            // .NET の SDK でビルドしたアプリは "1.0.0+<40桁のコミット>" になる。ビルドの特定に役立つので残し、先頭7桁に縮める
            e.AppVersion = Regex.Replace(v.ProductVersion ?? v.FileVersion ?? "", @"\+([0-9a-f]{7})[0-9a-f]{33}$", "+$1");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { } // 管理者権限で動くアプリなどは読めない

        var hwnd = target.MainWindowHandle;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var monitor = hwnd != 0 ? MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) : 0;
        if (monitor != 0 && GetMonitorInfo(monitor, ref info))
        {
            e.Screen = $"{info.rcMonitor.Width}×{info.rcMonitor.Height}";
            // GetDpiForWindow は DPI 非対応・システム DPI 対応のアプリだとメイン画面の DPI を返すので、モニターから取る
            e.ScalePercent = Monitors.ScalePercent(monitor);
        }
        return e;
    }
}
