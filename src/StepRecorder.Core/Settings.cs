using System.Text.Json;

namespace StepRecorder.Core;

public sealed class Settings
{
    public string OutputDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "step_recorder");
    public CaptureTrigger DefaultMode { get; set; } = CaptureTrigger.Click;
    public int DrawWaitMs { get; set; } = 300;
    public string HotkeyCapture { get; set; } = "Ctrl+Shift+F9";
    public string HotkeyPause { get; set; } = "Ctrl+Shift+F10";
    public string HotkeyStop { get; set; } = "Ctrl+Shift+F11";
    public GroupMode GroupMode { get; set; } = GroupMode.Sequence;
    /// <summary>起動時に GitHub Releases の新しい版を確認するか。</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>スクリーンショットにマウスカーソルを写すか。</summary>
    public bool CaptureCursor { get; set; }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "step_recorder", "settings.json");

    /// <summary>無い・壊れている場合は既定値。</summary>
    public static Settings Load(string path)
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), SessionStore.Options) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, SessionStore.Options));
    }

    /// <summary>新しい記録用フォルダ名(例: 20261121-174100_App)。</summary>
    public static string SessionFolderName(DateTimeOffset t, string processName) =>
        $"{t:yyyyMMdd-HHmmss}_{string.Concat(processName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c))}";
}
