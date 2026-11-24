using System.Globalization;
using System.Text.Json.Serialization;

namespace StepRecorder.Core;

/// <summary>画像内のピクセル座標で表す矩形。</summary>
public sealed record PixelRect(int X, int Y, int Width, int Height);

public sealed record PixelPoint(int X, int Y);

/// <summary>ステップを撮った契機。</summary>
public enum CaptureTrigger { Click, Manual, Video }

/// <summary>撮影モード。Video(録画)はクリックごとに撮りつつ、記録の最初から最後までを動画に録る。</summary>
public enum RecordMode { Click, Manual, Video }

public enum OperationKind { Click, TextInput, KeyPress }

public enum MouseButton { Left, Right, Middle }

public sealed record WindowInfo(string Title, string ClassName, string ProcessName);

/// <summary>UI Automation で取れた部品の情報。Rect は画像内の座標。</summary>
public sealed record ElementInfo(string Name, string ControlType, string AutomationId, PixelRect? Rect);

public sealed class Operation
{
    public OperationKind Kind { get; set; }
    public DateTimeOffset Time { get; set; }
    /// <summary>録画ボタンの動画の間の操作なら、その動画上の時刻(分割で振り分けるのに使う)。それ以外は null。</summary>
    public TimeSpan? VideoTime { get; set; }
    public ElementInfo? Element { get; set; }
    public MouseButton? Button { get; set; }
    public bool DoubleClick { get; set; }
    public PixelPoint? Point { get; set; }
    /// <summary>TextInput の入力内容(パスワード欄は伏せ字)。</summary>
    public string? Text { get; set; }
    /// <summary>KeyPress のキー表記(例: "Ctrl+S")。</summary>
    public string? Keys { get; set; }
}

/// <summary>1枚のスクリーンショットと、その画面で行った操作。</summary>
public sealed class Step
{
    public int Id { get; set; }
    public DateTimeOffset Time { get; set; }
    public CaptureTrigger Trigger { get; set; }
    /// <summary>session.json からの相対パス(例: "images/0001.png")。</summary>
    public string ImageFile { get; set; } = "";
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public WindowInfo Window { get; set; } = new("", "", "");
    public string ScreenName { get; set; } = "";
    public string Description { get; set; } = "";
    public List<Operation> Operations { get; set; } = [];
    public List<PixelRect> Highlights { get; set; } = [];
    /// <summary>トリミング範囲。null なら画像全体。</summary>
    public PixelRect? Crop { get; set; }

    /// <summary>
    /// 動画のステップなら、その動画(例: "videos/0003.mp4"、録画モードで切り出したものは "video.mp4")。
    /// ImageFile はポスター(再生前に見せる画像)になる。null なら静止画のステップ。
    /// </summary>
    public string? VideoFile { get; set; }
    /// <summary>動画のうち使う区間。null なら最初から/最後まで。</summary>
    public TimeSpan? VideoStart { get; set; }
    public TimeSpan? VideoEnd { get; set; }
    /// <summary>動画のステップの再生の速さ(2 なら倍速、0.5 なら半分)。null なら等速。使うときは Speed を見る。</summary>
    public double? VideoSpeed { get; set; }
    /// <summary>実際に使う速さ。session.json は手で直せるので、0 や極端な値は範囲に収める。</summary>
    [JsonIgnore]
    public double Speed => Math.Clamp(VideoSpeed ?? 1, PlaybackSpeed.Choices[0], PlaybackSpeed.Choices[^1]);
    /// <summary>配布用の書き出しと編集画面の再生で使うフレームレート。null なら録画したときのまま。録画したときより上げられない(使うときは FpsFor)。</summary>
    public int? VideoFps { get; set; }
    /// <summary>実際に使うフレームレート。recorded は動画ファイルのフレームレート。</summary>
    public int FpsFor(int recorded) => VideoFps is { } f && f > 0 && f < recorded ? f : recorded;
    /// <summary>録画モードで、このステップを撮ったときの全編の動画上の時刻。</summary>
    public TimeSpan? VideoTime { get; set; }
    /// <summary>録画ボタンで録った動画のステップで押したキー(この動画の時刻)。全編から切り出した区間は Session.Keys を使う。無ければ null。</summary>
    public List<KeyStroke>? Keys { get; set; }
    /// <summary>動画にキーを重ねない。既定(false)は重ねる(false は書かない)。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HideKeys { get; set; }
    /// <summary>録画ボタンで録った動画のステップでのクリック・ホイール(この動画の時刻と位置)。Keys と同じく、全編から切り出した区間は Session.Mouse を使う。</summary>
    public List<MouseStroke>? Mouse { get; set; }
    /// <summary>動画にマウスの操作を重ねない(キーとは別に選ぶ)。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HideMouse { get; set; }
    /// <summary>動画の音を使わない(再生・HTML では消音、配布用の mp4 には入れない)。既定(false)は使う(false は書かない)。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HideAudio { get; set; }
}

public sealed class Session
{
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>記録した対象(アプリのプロセス名か、モニターの名前)。</summary>
    public string TargetName { get; set; } = "";
    public EvidenceInfo Evidence { get; set; } = new();
    public List<Step> Steps { get; set; } = [];
    /// <summary>録画モードで録った全編の動画(例: "video.mp4")。録画モードでなければ null。</summary>
    public string? VideoFile { get; set; }
    /// <summary>録画モードで、全編の動画の間に押したキー(全編の動画の時刻)。無ければ null。</summary>
    public List<KeyStroke>? Keys { get; set; }
    /// <summary>録画モードで、全編の動画の間のクリック・ホイール(全編の動画の時刻と位置)。無ければ null。</summary>
    public List<MouseStroke>? Mouse { get; set; }
    /// <summary>残すものがあるか。録画モードでは、ステップが無くても全編の動画がある。</summary>
    [JsonIgnore]
    public bool HasContent => Steps.Count > 0 || VideoFile != null;
}

/// <summary>動画のステップの再生の速さ。</summary>
public static class PlaybackSpeed
{
    /// <summary>選べる速さ(遅い順)。</summary>
    public static readonly double[] Choices = [0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4, 8];

    public static string Label(double speed) => speed == 1 ? "等速" : $"{speed.ToString("0.##", CultureInfo.InvariantCulture)} 倍速";
}

/// <summary>動画のフレームレート。</summary>
public static class FrameRate
{
    /// <summary>選べるフレームレート(少ない順)。</summary>
    public static readonly int[] Choices = [5, 10, 15, 24, 30, 60];

    public const int Default = 15;

    /// <summary>設定のフレームレート。session.json・settings.json は手で直せるので、選べない値は既定にする。</summary>
    public static int Valid(int fps) => Choices.Contains(fps) ? fps : Default;

    /// <summary>録画したときが recorded の動画で選べるもの(それ以下)。</summary>
    public static int[] UpTo(int recorded) => [.. Choices.Where(f => f < recorded), recorded];

    public static string Label(int fps) => $"{fps}fps";
}

/// <summary>配布用に書き出すときの、動画のステップの形式。</summary>
public enum ClipFormat { Mp4, Gif }

/// <summary>エビデンスとして操作記録の冒頭に載せる、記録した環境の情報。取れなかった項目は空。</summary>
public sealed class EvidenceInfo
{
    /// <summary>対象アプリの exe のファイル名。</summary>
    public string AppFile { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string Os { get; set; } = "";
    /// <summary>対象アプリのウィンドウがあるモニターの解像度(例: "1920×1080")。</summary>
    public string Screen { get; set; } = "";
    /// <summary>そのモニターの拡大率(%)。0 は不明。</summary>
    public int ScalePercent { get; set; }
    public string RecorderVersion { get; set; } = "";
}

/// <summary>出力時の並べ方。</summary>
public enum GroupMode
{
    /// <summary>撮った順に並べる。</summary>
    Sequence,
    /// <summary>画面ごとにまとめ、その下に手順を並べる。</summary>
    ByScreen,
}
