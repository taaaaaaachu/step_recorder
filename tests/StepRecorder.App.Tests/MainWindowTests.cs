using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using static StepRecorder.App.Tests.Desktop;

namespace StepRecorder.App.Tests;

/// <summary>開始画面を実際に操作する。設定は記録開始などをしない限り保存されないので、利用者の設定は変わらない。</summary>
public sealed class MainWindowTests : IDisposable
{
    readonly Process app = Launch(AppExe);
    string[] expectedDialogs = [];

    public void Dispose() => Close(app, expectedDialogs);

    [E2EFact]
    public void ショートカットキー欄は押したキーの組み合わせが入る()
    {
        var main = app.MainWindowHandle;
        var before = ValueOf(main, "HkCapture");

        ClickOn(main, "HkCapture");
        Wait(300);
        Assert.Equal("キーを押してください", ValueOf(main, "HkCapture"));

        PressKeys(main, 0x41); // 修飾キーなしの A は受け付けない
        Wait(200);
        Assert.Equal("キーを押してください", ValueOf(main, "HkCapture"));

        PressKeys(main, 0x1B); // Esc で取り消すと元の値に戻る
        Wait(300);
        Assert.Equal(before, ValueOf(main, "HkCapture"));

        ClickOn(main, "HkCapture");
        Wait(300);
        // Alt を含む組み合わせ(WPF では Key.System として届く)。他のアプリが登録済みの組み合わせはそもそも届かないので、空いていそうなものを使う
        PressKeys(main, 0x11, 0x12, 0x78); // Ctrl+Alt+F9
        Wait(300);
        Assert.Equal("Ctrl+Alt+F9", ValueOf(main, "HkCapture"));

        ClickOn(main, "HkCapture");
        Wait(300);
        PressKeys(main, 0x11, 0x10, 0x4B); // Ctrl+Shift+K
        Wait(300);
        Assert.Equal("Ctrl+Shift+K", ValueOf(main, "HkCapture"));
    }

    [E2EFact]
    public void 今すぐ確認で結果が新しい見た目のメッセージボックスに出る()
    {
        expectedDialogs = ["インストールされていないため、更新を確認できません。"];
        ClickOn(app.MainWindowHandle, "CheckUpdate");
        // テストの実行はインストールしていないので、その旨が出る(メッセージボックスは持ち主の下にぶら下がる)
        Find(app.MainWindowHandle, new PropertyCondition(AutomationElement.NameProperty, "インストールされていないため、更新を確認できません。"));
        // マニフェストでコモンコントロール v6 を指定していれば、メッセージボックスはそれで描かれる
        app.Refresh();
        Assert.Contains(app.Modules.Cast<ProcessModule>(), m =>
            m.ModuleName.Equals("comctl32.dll", StringComparison.OrdinalIgnoreCase) && m.FileVersionInfo.FileMajorPart >= 6);
    }
}


/// <summary>開始画面の設定のうち、記録を始めなくても残すもの。入力は送らない。</summary>
public sealed class StartSettingsTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "sr-start-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    [DesktopFact]
    public void 出力先は記録を始めずに閉じても残る()
    {
        MakeProfile(root, new StepRecorder.Core.Settings { AutoUpdate = false, OutputDir = Path.Combine(root, "before") });
        var app = Open(AppExe, "", root);
        try
        {
            var after = Path.Combine(root, "after");
            var box = Find(app.MainWindowHandle, "OutputDir");
            ((ValuePattern)box.GetCurrentPattern(ValuePattern.Pattern)).SetValue(after);
            ((WindowPattern)AutomationElement.FromHandle(app.MainWindowHandle).GetCurrentPattern(WindowPattern.Pattern)).Close();
            Assert.True(app.WaitForExit(5000), "閉じない");
            Assert.Equal(after, StepRecorder.Core.Settings.Load(Path.Combine(root, @"AppData\Roaming\step_recorder\settings.json")).OutputDir);
        }
        finally { Close(app); }
    }
}
