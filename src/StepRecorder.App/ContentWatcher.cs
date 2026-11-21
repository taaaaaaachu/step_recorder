using System.Runtime.InteropServices;
using System.Windows.Automation;
using StepRecorder.Core;
using UiaCom = Interop.UIAutomationClient;

namespace StepRecorder.App;

/// <summary>
/// 対象アプリのウィンドウ内の表示の切り替わりを UI Automation で監視する。
/// 部品の追加・削除(構造変化)とタブの選択を拾い、changed(ウィンドウ, 選ばれたタブ) で知らせる。
/// changed は UI Automation のスレッドから呼ばれる。
/// 構造変化は COM 版(IUIAutomation)で受ける。.NET の System.Windows.Automation は、runtimeId を null で送ってくる
/// アプリがあると、ハンドラーに届く前のフレームワークの中で例外になり、プロセスごと落ちるため。
/// </summary>
sealed class ContentWatcher(Action<nint, ElementInfo?> changed)
{
    const int TimeoutMs = 2000;
    readonly HashSet<nint> watched = [];
    readonly UiaCom.IUIAutomation automation = new UiaCom.CUIAutomation8();

    /// <summary>ウィンドウを監視対象に加える。同じウィンドウは一度だけ。ワーカースレッドから呼ぶ。</summary>
    public void Watch(nint hwnd)
    {
        if (hwnd == 0 || !watched.Add(hwnd)) return;
        // UI Automation のイベント登録は MTA のスレッドから行う
        Task.Run(() =>
        {
            try
            {
                automation.AddStructureChangedEventHandler(automation.ElementFromHandle(hwnd), UiaCom.TreeScope.TreeScope_Subtree, null,
                    new StructureHandler(() => changed(hwnd, null)));
                Automation.AddAutomationEventHandler(SelectionItemPattern.ElementSelectedEvent, AutomationElement.FromHandle(hwnd), TreeScope.Subtree,
                    (sender, _) => { if (TabOf(sender) is { } tab) changed(hwnd, tab); });
            }
            catch (Exception e) when (e is ElementNotAvailableException or COMException) { } // 登録の前に閉じられた
        }).Wait(TimeoutMs);
    }

    public void Stop() => Task.Run(() =>
    {
        automation.RemoveAllEventHandlers();
        Automation.RemoveAllEventHandlers();
    }).Wait(TimeoutMs);

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

    sealed class StructureHandler(Action changed) : UiaCom.IUIAutomationStructureChangedEventHandler
    {
        public void HandleStructureChangedEvent(UiaCom.IUIAutomationElement sender, UiaCom.StructureChangeType changeType, int[] runtimeId) => changed();
    }
}
