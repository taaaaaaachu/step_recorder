namespace StepRecorder.Core;

/// <summary>中身の違うアプリが同じ exe で動く、共通のホスト。exe が同じでも同じアプリとはみなさない。</summary>
public static class SharedHost
{
    // ponytail: 名前の一覧で見分ける。漏れたホストでは別のアプリも一緒に記録されるので、見つかったら足す
    static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost", "WWAHost", "msedgewebview2", // UWP・Web アプリ
        "java", "javaw", "dotnet", "node", "electron", "python", "pythonw",
        "mshta", "wscript", "cscript", "mmc", "rundll32", "dllhost",
        "conhost", "cmd", "powershell", "pwsh",
    };

    public static bool Is(string exePath)
    {
        var name = Path.GetFileName(exePath);
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        // python3.exe・python3.12.exe のようなバージョン付きの名前も
        return Names.Contains(name) || name.StartsWith("python3", StringComparison.OrdinalIgnoreCase);
    }
}
