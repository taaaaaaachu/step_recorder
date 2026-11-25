using System.Windows;
using Velopack;
using Velopack.Sources;

namespace StepRecorder.App;

/// <summary>GitHub Releases からの自動更新。</summary>
static class Updater
{
    public const string Repo ="https://github.com/taaaaaaachu/step_recorder";

    static readonly UpdateManager Manager = new(new GithubSource(Repo, null, false));

    /// <summary>
    /// 更新があれば確認してから適用・再起動する。
    /// 起動時の確認(manual=false)では、更新がないときも通信に失敗したときも何も言わない(次回の起動で再確認)。
    /// 手動の確認では、その結果も知らせる。
    /// </summary>
    public static async Task CheckAsync(Window owner, bool manual = false)
    {
        void Tell(string text) { if (manual) MessageBox.Show(owner, text, "step_recorder"); }
        if (!Manager.IsInstalled) { Tell("インストールされていないため、更新を確認できません。"); return; } // dotnet run など
        try
        {
            var info = await Manager.CheckForUpdatesAsync();
            if (info == null) { Tell($"最新のバージョン({AppVersion.Current})です。"); return; }
            var ok = MessageBox.Show(owner, $"新しいバージョン {info.TargetFullRelease.Version} があります。更新して再起動しますか?",
                "step_recorder", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (ok != MessageBoxResult.Yes) return;
            await Manager.DownloadUpdatesAsync(info);
            Manager.ApplyUpdatesAndRestart(info);
        }
        catch (Exception e) { Tell($"更新を確認できませんでした。\n{e.Message}"); }
    }
}
