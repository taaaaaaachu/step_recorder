using System.Windows.Interop;
using System.Windows.Threading;
using StepRecorder.Core;

namespace StepRecorder.App.Tests;

public sealed class MonitorBadgeTests
{
    const int WM_WINDOWPOSCHANGED = 0x0047;

    [DesktopFact]
    public void 枠は見えた瞬間から指定した画面にある()
    {
        var seen = new List<(DisplayMonitor Monitor, PixelRect Rect)>();
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var monitor in Monitors.All())
                {
                    var badge = new MonitorBadge(monitor);
                    void Note(nint window)
                    {
                        if (Native.IsWindowVisible(window) && Native.GetWindowRect(window, out var r)) seen.Add((monitor, r.ToPixelRect()));
                    }
                    var hwnd = new WindowInteropHelper(badge).EnsureHandle();
                    // 見えている間に位置か大きさが変われば、全部控える
                    HwndSource.FromHwnd(hwnd).AddHook((nint window, int message, nint w, nint l, ref bool handled) =>
                    {
                        if (message == WM_WINDOWPOSCHANGED) Note(window);
                        return 0;
                    });
                    badge.Show();
                    Note(hwnd); // Show から戻った時点で、もう見えている

                    var rendered = new DispatcherFrame();
                    badge.Dispatcher.BeginInvoke(() => rendered.Continue = false, DispatcherPriority.ApplicationIdle);
                    Dispatcher.PushFrame(rendered);
                    Note(hwnd);
                    badge.Close();
                }
            }
            catch (Exception e) { error = e; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(error);
        Assert.NotEmpty(seen);
        var strays = seen.Where(s => s.Rect != s.Monitor.Bounds).Distinct().Select(s => $"{s.Monitor.Title}: {s.Rect}").ToList();
        Assert.True(strays.Count == 0, $"別の所に出た: {string.Join(" / ", strays)}");
    }
}
