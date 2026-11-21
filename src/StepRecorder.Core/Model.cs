namespace StepRecorder.Core;

/// <summary>画像内のピクセル座標で表す矩形。</summary>
public sealed record PixelRect(int X, int Y, int Width, int Height);

public sealed record PixelPoint(int X, int Y);

/// <summary>スクリーンショットを撮った契機。</summary>
public enum CaptureTrigger { Click, Manual, WindowChange }

public enum OperationKind { Click, TextInput, KeyPress, WindowShown, ContentChanged }

public enum MouseButton { Left, Right, Middle }

public sealed record WindowInfo(string Title, string ClassName, string ProcessName);

/// <summary>UI Automation で取れた部品の情報。Rect は画像内の座標。</summary>
public sealed record ElementInfo(string Name, string ControlType, string AutomationId, PixelRect? Rect);

public sealed class Operation
{
    public OperationKind Kind { get; set; }
    public DateTimeOffset Time { get; set; }
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
}

public sealed class Session
{
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public string ProcessName { get; set; } = "";
    public EvidenceInfo Evidence { get; set; } = new();
    public List<Step> Steps { get; set; } = [];
}

/// <summary>エビデンスとして画面一覧の冒頭に載せる、記録した環境の情報。取れなかった項目は空。</summary>
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
