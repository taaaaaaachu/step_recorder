namespace StepRecorder.Core;

/// <summary>記録の対象に選べるモニター。Bounds はスクリーン上の物理ピクセル。</summary>
public sealed record DisplayMonitor(nint Handle, int Number, PixelRect Bounds, bool Primary)
{
    /// <summary>記録フォルダと表題に使う名前(例: "画面2")。</summary>
    public string Name => $"画面{Number}";

    /// <summary>画面に出す名前(例: "画面 2")。</summary>
    public string Title => $"画面 {Number}";

    /// <summary>対象の一覧に出す文言。</summary>
    public string Label => $"{Title}  {Bounds.Width}×{Bounds.Height}{(Primary ? "(メイン)" : "")}";

    /// <summary>左から右、同じ列なら上から下の順に番号を振る。</summary>
    public static IReadOnlyList<DisplayMonitor> Numbered(IEnumerable<(nint Handle, PixelRect Bounds, bool Primary)> monitors) =>
        monitors.OrderBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y)
            .Select((m, i) => new DisplayMonitor(m.Handle, i + 1, m.Bounds, m.Primary)).ToList();
}
