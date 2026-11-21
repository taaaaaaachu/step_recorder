using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using static StepRecorder.App.Tests.Desktop;

namespace StepRecorder.App.Tests;

/// <summary>開始画面を実際に操作する。設定は記録開始などをしない限り保存されないので、利用者の設定は変わらない。</summary>
public sealed class MainWindowTests : IDisposable
{
    readonly Process app = Launch(Path.Combine(AppContext.BaseDirectory, "StepRecorder.exe"));

    public void Dispose()
    {
        try { app.Kill(); app.WaitForExit(5000); } catch (InvalidOperationException) { }
    }

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
    public void 撮影モードに録画がある() =>
        Find(app.MainWindowHandle, new PropertyCondition(AutomationElement.NameProperty, "録画"));

    [E2EFact]
    public void 今すぐ確認で結果が新しい見た目のメッセージボックスに出る()
    {
        ClickOn(app.MainWindowHandle, "CheckUpdate");
        // テストの実行はインストールしていないので、その旨が出る(メッセージボックスは持ち主の下にぶら下がる)
        Find(app.MainWindowHandle, new PropertyCondition(AutomationElement.NameProperty, "インストールされていないため、更新を確認できません。"));
        // マニフェストでコモンコントロール v6 を指定していれば、メッセージボックスはそれで描かれる
        app.Refresh();
        Assert.Contains(app.Modules.Cast<ProcessModule>(), m =>
            m.ModuleName.Equals("comctl32.dll", StringComparison.OrdinalIgnoreCase) && m.FileVersionInfo.FileMajorPart >= 6);
    }
}
