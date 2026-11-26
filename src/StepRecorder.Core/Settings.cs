using System.Text.Json;

namespace StepRecorder.Core;

public sealed class Settings
{
    public string OutputDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "step_recorder");
    public RecordMode DefaultMode { get; set; } = RecordMode.Click;
    public string HotkeyCapture { get; set; } = "Ctrl+Shift+F9";
    public string HotkeyPause { get; set; } = "Ctrl+Shift+F10";
    public string HotkeyStop { get; set; } = "Ctrl+Shift+F11";
    public string HotkeyClip { get; set; } = "Ctrl+Shift+F12";
    /// <summary>配布用に書き出すときの動画の形式(前回の選択)。</summary>
    public ClipFormat ClipFormat { get; set; }
    public GroupMode GroupMode { get; set; } = GroupMode.Sequence;
    /// <summary>起動時に GitHub Releases の新しいバージョンを確認するか。</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>スクリーンショットにマウスカーソルを写すか。</summary>
    public bool CaptureCursor { get; set; }
    /// <summary>編集画面で、ステップ一覧が一覧と編集欄の幅の合計に占める割合。</summary>
    public double StepListRatio { get; set; } = 0.25;
    /// <summary>録画のフレームレート(FrameRate.Choices のどれか)。</summary>
    public int Fps { get; set; } = FrameRate.Default;

    /// <summary>設定ファイルの場所。portable 版では起動時に展開フォルダのものへ差し替える。</summary>
    public static string DefaultPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "step_recorder", "settings.json");

    /// <summary>無い・壊れている場合は既定値。</summary>
    public static Settings Load(string path)
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), SessionStore.Options) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    /// <summary>
    /// 設定を変えるときは必ずこれを使う。今のファイルを読み直してから変える所だけを変える
    /// (前に読んだものを丸ごと書くと、他の画面が保存した値を戻してしまう)。
    /// </summary>
    public static Settings Update(string path, Action<Settings> change)
    {
        var settings = Load(path);
        change(settings);
        settings.Save(path);
        return settings;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, SessionStore.Options));
    }

    /// <summary>新しい記録用フォルダ名(例: 20261121-174100_App)。</summary>
    public static string SessionFolderName(DateTimeOffset t, string targetName) =>
        $"{t:yyyyMMdd-HHmmss}_{string.Concat(targetName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c))}";
}
