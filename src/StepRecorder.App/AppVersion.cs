using System.Reflection;

namespace StepRecorder.App;

static class AppVersion
{
    /// <summary>
    /// このアプリのバージョン。リリースでは CI がタグのバージョン(-p:Version)を埋め込み、手元のビルドは 0.0.0-dev になる。
    /// ビルド時に付く "+コミット" は表示しない。
    /// </summary>
    public static string Current { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";
}
