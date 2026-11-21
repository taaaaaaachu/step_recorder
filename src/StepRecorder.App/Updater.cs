using System.Windows;
using Velopack;
using Velopack.Sources;

namespace StepRecorder.App;

/// <summary>GitHub Releases からの自動更新。</summary>
static class Updater
{
    const string Repo = "https://github.com/taaaaaaachu/step_recorder";

    static readonly UpdateManager Manager = new(new GithubSource(Repo, null, false));

    /// <summary>起動時に呼ぶ。更新があれば確認してから適用・再起動する。通信の失敗は無視する(次回の起動で再確認)。</summary>
    public static async Task CheckAsync(Window owner)
    {
        if (!Manager.IsInstalled) return; // dotnet run などインストールしていない実行では何もしない
        try
        {
            var info = await Manager.CheckForUpdatesAsync();
            if (info == null) return;
            var ok = MessageBox.Show(owner, $"新しいバージョン {info.TargetFullRelease.Version} があります。更新して再起動しますか?",
                "step_recorder", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (ok != MessageBoxResult.Yes) return;
            await Manager.DownloadUpdatesAsync(info);
            Manager.ApplyUpdatesAndRestart(info);
        }
        catch (Exception) { }
    }
}
