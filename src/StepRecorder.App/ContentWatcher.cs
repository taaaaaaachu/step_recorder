using System.Windows.Automation;
using StepRecorder.Core;

namespace StepRecorder.App;

/// <summary>
/// 対象アプリのウィンドウ内の表示の切り替わりを UI Automation で監視する。
/// 部品の追加・削除(構造変化)とタブの選択を拾い、changed(ウィンドウ, 選ばれたタブ) で知らせる。
/// changed は UI Automation のスレッドから呼ばれる。
/// </summary>
sealed class ContentWatcher(Action<nint, ElementInfo?> changed)
{
    const int TimeoutMs = 2000;
    readonly HashSet<nint> watched = [];

    /// <summary>ウィンドウを監視対象に加える。同じウィンドウは一度だけ。ワーカースレッドから呼ぶ。</summary>
    public void Watch(nint hwnd)
    {
        if (hwnd == 0 || !watched.Add(hwnd)) return;
        // UI Automation のイベント登録は MTA のスレッドから行う
        Task.Run(() =>
        {
            try
            {
                var window = AutomationElement.FromHandle(hwnd);
                Automation.AddStructureChangedEventHandler(window, TreeScope.Subtree, (_, _) => changed(hwnd, null));
                Automation.AddAutomationEventHandler(SelectionItemPattern.ElementSelectedEvent, window, TreeScope.Subtree,
                    (sender, _) => { if (TabOf(sender) is { } tab) changed(hwnd, tab); });
            }
            catch (ElementNotAvailableException) { } // 登録の前に閉じられた
        }).Wait(TimeoutMs);
    }

    public void Stop() => Task.Run(Automation.RemoveAllEventHandlers).Wait(TimeoutMs);

    /// <summary>選ばれたのがタブならその情報。リストの項目などの選択は表示の切り替わりとみなさない。</summary>
    static ElementInfo? TabOf(object sender)
    {
        try
        {
            return sender is AutomationElement e && e.Current.ControlType == ControlType.TabItem
                ? new ElementInfo(e.Current.Name, "TabItem", e.Current.AutomationId, null)
                : null;
        }
        catch (ElementNotAvailableException) { return null; }
    }
}
