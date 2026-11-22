using System.ComponentModel;
using System.Diagnostics;
using StepRecorder.Core;
using static StepRecorder.App.Native;

namespace StepRecorder.App;

/// <summary>何を記録するか。どの操作を拾い、どう撮り、どう録るかは対象ごとに違う。</summary>
abstract class CaptureTarget
{
    /// <summary>記録フォルダと画面一覧の表題に使う名前。</summary>
    public abstract string Name { get; }

    /// <summary>今も記録できるか(終了していない・外されていない)。</summary>
    public abstract bool IsAvailable { get; }

    /// <summary>画面に見えているものを撮るか。撮るなら、自分のバーなどが写らないようにする必要がある。</summary>
    public virtual bool CapturesScreen => false;

    /// <summary>記録を始めたときに操作しているとみなすウィンドウ。</summary>
    public virtual nint InitialWindow => 0;

    public abstract EvidenceInfo CollectEvidence();

    /// <summary>このクリックを記録するか。フックのスレッドから呼ぶのですぐ返す。</summary>
    public abstract bool OwnsClick(nint window, POINT point);

    /// <summary>foreground へのキー入力を記録するか。フックのスレッドから呼ぶのですぐ返す。</summary>
    public abstract bool OwnsKey(nint foreground);

    /// <summary>手動撮影・録画ボタンで操作中とみなすウィンドウ。撮れるものが無ければ false。last は最後に撮ったウィンドウ。</summary>
    public abstract bool TryGetActiveWindow(nint last, out nint window);

    public abstract Shot? Capture(nint window, bool cursor);

    public abstract VideoRecorder StartVideo(nint window, string path, bool cursor, Func<bool> paused);

    /// <summary>ステップに載せるウィンドウの情報。</summary>
    public abstract WindowInfo Describe(nint window);
}

/// <summary>アプリ(プロセス)。そのウィンドウだけを撮るので、他のアプリや常駐バーは写らない。</summary>
sealed class AppTarget(Process process) : CaptureTarget
{
    readonly uint pid = (uint)process.Id;
    readonly string name = process.ProcessName;

    public override string Name => name;

    public override bool IsAvailable => !process.HasExited;

    public override nint InitialWindow => process.MainWindowHandle;

    public override EvidenceInfo CollectEvidence() => EvidenceCollector.Collect(process);

    public override bool OwnsClick(nint window, POINT point) => ProcessId(window) == pid;

    public override bool OwnsKey(nint foreground) => ProcessId(foreground) == pid;

    /// <summary>前面が対象プロセスならそれ、違えば最後に撮ったウィンドウ。</summary>
    public override bool TryGetActiveWindow(nint last, out nint window)
    {
        window = GetAncestor(GetForegroundWindow(), GA_ROOT);
        if (ProcessId(window) == pid) return true;
        if (IsWindow(last) && IsWindowVisible(last)) window = last;
        else
        {
            try { window = Process.GetProcessById((int)pid).MainWindowHandle; }
            catch (ArgumentException) { window = 0; }
        }
        return window != 0;
    }

    public override Shot? Capture(nint window, bool cursor) => WindowCapture.Capture(window, cursor);

    public override VideoRecorder StartVideo(nint window, string path, bool cursor, Func<bool> paused) =>
        VideoRecorder.Start(window, path, cursor, paused);

    public override WindowInfo Describe(nint window) => new(Text(window), ClassName(window), name);
}

/// <summary>モニター。見えているものを丸ごと撮り、その上でした操作を、どのアプリへのものでも記録する(step_recorder 自身は除く)。</summary>
sealed class MonitorTarget(DisplayMonitor monitor) : CaptureTarget
{
    readonly RECT bounds = ToRect(monitor.Bounds);

    public DisplayMonitor Monitor => monitor;

    public override string Name => monitor.Name;

    public override bool CapturesScreen => true;

    /// <summary>一覧に出したときと同じ位置・大きさでつながっているか。</summary>
    public override bool IsAvailable => Monitors.Info(monitor.Handle) is { } info && info.rcMonitor.ToPixelRect() == monitor.Bounds;

    public override EvidenceInfo CollectEvidence() => EvidenceCollector.Collect(monitor);

    public override bool OwnsClick(nint window, POINT point) =>
        MonitorFromPoint(point, MONITOR_DEFAULTTONULL) == monitor.Handle && ProcessId(window) != Environment.ProcessId;

    // ponytail: 2 つのモニターにまたがるウィンドウへのキー入力は、広く載っている方のモニターのものとみなす
    public override bool OwnsKey(nint foreground) =>
        foreground != 0 && MonitorFromWindow(foreground, MONITOR_DEFAULTTONULL) == monitor.Handle && ProcessId(foreground) != Environment.ProcessId;

    /// <summary>前面がこのモニターに無くても、モニター全体は撮れる。</summary>
    public override bool TryGetActiveWindow(nint last, out nint window)
    {
        var fg = GetAncestor(GetForegroundWindow(), GA_ROOT);
        window = OwnsKey(fg) ? fg : 0;
        return true;
    }

    public override Shot? Capture(nint window, bool cursor) => WindowCapture.CaptureMonitor(bounds, window, cursor);

    public override VideoRecorder StartVideo(nint window, string path, bool cursor, Func<bool> paused) =>
        VideoRecorder.StartMonitor(monitor.Handle, bounds, path, cursor, paused);

    /// <summary>プロセス名は操作したウィンドウのもの(画面一覧モードでまとめる単位になる)。題の無いもの(タスクバーなど)はモニターの名前にする。</summary>
    public override WindowInfo Describe(nint window)
    {
        if (window == 0) return new(monitor.Title, "", "");
        var title = Text(window);
        return new(title == "" ? monitor.Title : title, ClassName(window), ProcessNameOf(window));
    }

    static string ProcessNameOf(nint window)
    {
        try
        {
            using var process = Process.GetProcessById((int)ProcessId(window));
            return process.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { return ""; } // もう終了している
    }
}
