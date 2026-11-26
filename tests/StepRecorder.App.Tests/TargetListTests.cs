using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using static StepRecorder.App.Tests.Desktop;

namespace StepRecorder.App.Tests;

/// <summary>開始画面の対象の一覧。UI Automation で選ぶので、入力は送らない。記録は始めないので設定も変わらない。</summary>
public sealed class TargetListTests : IDisposable
{
    Process? app;

    public void Dispose() => Close(app);

    [DesktopFact]
    public void 一覧のモニターを選ぶと_そのモニターに枠が出て消える()
    {
        app = Open(AppExe);
        // 逃がした設定で動いていること(利用者の設定を書き換えない)
        Assert.Equal(Path.Combine(Profile, "records"), ValueOf(app.MainWindowHandle, "OutputDir"));
        var combo = Find(app.MainWindowHandle, "Targets");
        var list = (ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern);

        foreach (var m in Monitors.All())
        {
            list.Expand();
            Wait(300);
            var item = combo.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, m.Label));
            Assert.NotNull(item); // 読み上げ名も一覧の文言になる
            var title = $"step_recorder {m.Title}";
            var select = (SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern);
            // 実アプリでも、見えた瞬間の位置を見る(選ぶ前から休まずに見張る)
            var first = Task.Factory.StartNew(() =>
            {
                for (var clock = Stopwatch.StartNew(); clock.ElapsedMilliseconds < 2000;)
                    if (FindWindow(null, title) is var h && h != 0 && Native.IsWindowVisible(h) && Native.GetWindowRect(h, out var r)) return r.ToPixelRect();
                return null;
            }, TaskCreationOptions.LongRunning);
            select.Select();
            Assert.Equal(m.Bounds, first.Result); // 別の画面に出てから飛ばない
            list.Collapse();

            Assert.True(Until(() => FindWindow(null, title) is var h && h != 0 && Native.IsWindowVisible(h)
                && Native.GetWindowRect(h, out var r) && r.ToPixelRect() == m.Bounds, 2000), $"{m.Label} に枠が出ない");

            var badge = FindWindow(null, title);
            var style = (long)Native.GetWindowLongPtr(badge, Native.GWL_EXSTYLE);
            Assert.Equal(Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE, style & (Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE));
            Assert.True(GetWindowDisplayAffinity(badge, out var affinity) && affinity == 0x11, "取り込みに写ってしまう");
            Assert.True(Until(() => FindWindow(null, title) == 0, 3500), $"{m.Label} の枠が消えない");
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint FindWindow(string? className, string? title);
    [DllImport("user32.dll")] static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
}
