using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace StepRecorder.App.Tests;

/// <summary>
/// プロセスに出たダイアログ(#32770。メッセージボックスやファイルを選ぶ画面と、その上に出るエラー)を、題名と文言で覚える。
/// 出てすぐ閉じたものも拾えるよう、50ms ごとに見る。プロセスが終わったら見るのをやめる。
/// </summary>
sealed class DialogWatch
{
    readonly Process process;
    readonly Timer timer;
    readonly HashSet<nint> known = [];
    readonly List<string> seen = [];

    public DialogWatch(Process process)
    {
        this.process = process;
        timer = new Timer(_ => Poll(), null, 0, 50);
    }

    /// <summary>見るのをやめ、これまでに出たダイアログ(「題名: 文言」)を返す。</summary>
    public List<string> Stop()
    {
        timer.Dispose();
        Poll();
        lock (known) return [.. seen];
    }

    void Poll()
    {
        lock (known)
        {
            if (process.HasExited) { timer.Dispose(); return; }
            EnumWindows((h, _) =>
            {
                if (Native.ProcessId(h) == process.Id && Native.IsWindowVisible(h) && Native.ClassName(h) == "#32770" && known.Add(h)) seen.Add(Describe(h));
                return true;
            }, 0);
        }
    }

    static string Describe(nint h)
    {
        try
        {
            var texts = AutomationElement.FromHandle(h).FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                .Cast<AutomationElement>().Select(e => e.Current.Name).Where(t => t.Length > 0);
            return $"{Native.Text(h)}: {string.Join(" ", texts)}";
        }
        catch (ElementNotAvailableException) { return Native.Text(h); } // 読む前に閉じた
    }

    [DllImport("user32.dll")] static extern bool EnumWindows(Native.EnumWindowsProc proc, nint lParam);
}
