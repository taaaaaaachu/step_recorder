using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using StepRecorder.Core;
using static StepRecorder.App.Tests.Desktop;

namespace StepRecorder.App.Tests;

/// <summary>設定の保存。利用者の settings.json に触らないよう、子プロセスの USERPROFILE を一時フォルダに逃がす。入力は送らない。</summary>
public sealed class SettingsPersistTests : IDisposable
{
    readonly string profile = Path.Combine(Path.GetTempPath(), "sr-profile-" + Guid.NewGuid());
    Process? app;

    public void Dispose()
    {
        Close(app, "開く:"); // 最後に出す、記録を開く画面(出る前に終わることもある)
        if (Directory.Exists(profile)) Directory.Delete(profile, true);
    }

    [DesktopFact]
    public void 開始画面の保存で_編集画面で選んだ動画の形式を戻さない()
    {
        var path = Path.Combine(profile, @"AppData\Roaming\step_recorder\settings.json");
        var output = Path.Combine(profile, "records");
        Directory.CreateDirectory(output);
        MakeProfile(profile, new Settings { OutputDir = output, ClipFormat = ClipFormat.Gif, AutoUpdate = false, Fps = 30 });

        app = Open(AppExe, profile: profile);
        var main = app.MainWindowHandle;
        // 逃がせていなければ、利用者の設定を書く前にやめる
        Assert.Equal(output, ValueOf(main, "OutputDir"));

        // 起動したあとで、編集画面が形式を MP4 に変えて保存した
        var changed = Settings.Load(path);
        changed.ClipFormat = ClipFormat.Mp4;
        changed.Save(path);

        // フレームレートは保存した値で出て、選び直すと保存される
        var fps = Find(main, "Fps");
        Assert.Equal("30fps", ((SelectionPattern)fps.GetCurrentPattern(SelectionPattern.Pattern)).Current.GetSelection()[0].Current.Name);
        var expand = (ExpandCollapsePattern)fps.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        expand.Expand();
        Wait(300);
        ((SelectionItemPattern)fps.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
            new PropertyCondition(AutomationElement.NameProperty, "60fps"))).GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        expand.Collapse();

        ((TogglePattern)Find(main, "CaptureCursor").GetCurrentPattern(TogglePattern.Pattern)).Toggle(); // 保存されたことの目印
        var open = (InvokePattern)Find(main, new PropertyCondition(AutomationElement.NameProperty, "記録を開く...")).GetCurrentPattern(InvokePattern.Pattern);
        _ = Task.Run(open.Invoke); // 設定を保存してからファイル選択を出す(出ている間は返らないことがある)

        Assert.True(Until(() => Settings.Load(path).CaptureCursor), "保存されない");
        Assert.Equal(ClipFormat.Mp4, Settings.Load(path).ClipFormat);
        Assert.Equal(60, Settings.Load(path).Fps);
    }
}
