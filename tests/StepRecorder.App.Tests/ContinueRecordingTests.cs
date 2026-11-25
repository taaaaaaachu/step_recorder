using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using StepRecorder.Core;
using static StepRecorder.App.Tests.Desktop;

namespace StepRecorder.App.Tests;

/// <summary>編集画面の「続けて記録」から、開始画面を経て同じ記録に足す。入力は送らず UI Automation で押す。</summary>
public sealed class ContinueRecordingTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "sr-continue-" + Guid.NewGuid());
    string Dir => Path.Combine(root, "records", "20261125-100000_Demo");
    Process? app, sample;

    public void Dispose()
    {
        Close(app);
        try { sample?.Kill(); } catch (InvalidOperationException) { }
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    /// <summary>step_recorder のトップレベルのウィンドウのうち、題が match に合うもの。出るまで待つ。</summary>
    AutomationElement Window(Func<string, bool> match)
    {
        AutomationElement? found = null;
        Assert.True(Until(() =>
        {
            found = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, app!.Id))
                .Cast<AutomationElement>().FirstOrDefault(w => match(w.Current.Name));
            return found != null;
        }, 10000), "ウィンドウが出ない");
        return found!;
    }

    static void Press(AutomationElement window, string name)
    {
        ((InvokePattern)window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name))
            .GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        Wait(500);
    }

    static bool IsEditor(string title) => title.StartsWith("step_recorder 編集");

    [DesktopFact]
    public void 続けて記録で何も撮らずに止めても_記録は消えずに編集画面に戻る()
    {
        // ステップを全部消して保存した記録(何も足さずに止めると、新しい記録なら空なのでフォルダごと消す)
        SessionStore.Save(new Session { TargetName = "Demo", CreatedAt = DateTimeOffset.Now }, Dir);
        File.WriteAllText(Path.Combine(Dir, Export.HtmlFile), "");

        // ホットキーは利用者のものとぶつからない組み合わせ。何も撮らないよう手動撮影で始める
        MakeProfile(root, new Settings
        {
            AutoUpdate = false, OutputDir = Path.Combine(root, "records"), DefaultMode = RecordMode.Manual,
            HotkeyCapture = "Ctrl+Alt+Shift+F13", HotkeyPause = "Ctrl+Alt+Shift+F14", HotkeyStop = "Ctrl+Alt+Shift+F15", HotkeyClip = "Ctrl+Alt+Shift+F16",
        });
        sample = Open(SampleExe);
        app = Open(AppExe, $"\"{Dir}\"", root);

        // 続けて記録 → 開始画面に、どの記録に足すかが出る。やめると編集画面に戻る
        Press(Window(IsEditor), "続けて記録");
        var main = Window(t => t == "step_recorder");
        Assert.Contains("「Demo」の記録に続けて記録します", main.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "AppendLabel")).Current.Name);
        Press(main, "やめて編集に戻る");
        Press(Window(IsEditor), "続けて記録");

        // テスト用アプリを選んで始め、何も撮らずに止める
        main = Window(t => t == "step_recorder");
        var targets = main.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "Targets"));
        ((ExpandCollapsePattern)targets.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        Wait(500);
        var item = targets.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>().First(i => i.Current.Name.StartsWith("SampleTarget"));
        ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        ((ExpandCollapsePattern)targets.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse();
        Wait(300);
        Press(main, "記録開始");
        Press(Window(t => t == "step_recorder 記録中"), "停止");

        Window(IsEditor);
        Assert.Equal("Demo", SessionStore.Load(Dir).TargetName);
        Assert.True(File.Exists(Path.Combine(Dir, Export.HtmlFile)));
    }
}
