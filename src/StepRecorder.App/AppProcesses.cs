using StepRecorder.Core;
using static StepRecorder.App.Native;

namespace StepRecorder.App;

/// <summary>
/// 1 つのアプリとみなすプロセスの範囲と、その中で今どのウィンドウを相手にするか。
/// 1 ウィンドウ(1 タブ)が 1 プロセスのアプリ(サクラエディタなど。見えていないタブのウィンドウは隠される)があるので、
/// pid ではなく exe のフルパスでまとめる。共通のホスト(SharedHost)と、パスの取れないものは pid で見る。
/// </summary>
sealed class AppProcesses
{
    readonly uint pid;
    readonly string? path; // null なら pid だけで見る

    /// <summary>最初に渡されたプロセス(録音するプロセスの木の根)。</summary>
    public uint Pid => pid;

    public AppProcesses(uint pid)
    {
        this.pid = pid;
        if (ProcessPath(pid) is { } exe && !SharedHost.Is(exe)) path = exe;
    }

    /// <summary>同じアプリのウィンドウか。フックのスレッドからも呼ぶ。</summary>
    public bool Contains(nint window) => ContainsProcess(ProcessId(window));

    bool ContainsProcess(uint other)
    {
        if (other == 0) return false;
        if (path == null) return other == pid;
        if (other != pid && other == Environment.ProcessId) return false; // step_recorder 自身は巻き込まない
        return string.Equals(ProcessPath(other), path, StringComparison.OrdinalIgnoreCase); // 終了した pid の使い回しにも惑わされない
    }

    public bool IsSameApp(AppProcesses other) => path == null ? pid == other.pid : string.Equals(path, other.path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 相手にするメインウィンドウ。prefer が見えていればそれ。違えば見えているものから、
    /// className と同じ種類・持ち主の無いもの・最小化していないものを優先して、手前のもの。無ければ 0。
    /// </summary>
    public nint MainWindow(nint prefer = 0, string? className = null)
    {
        if (IsShown(prefer) && !IsIconic(prefer) && Contains(prefer)) return prefer;
        var (best, bestRank) = ((nint)0, -1);
        for (var h = GetTopWindow(0); h != 0; h = GetWindow(h, GW_HWNDNEXT))
        {
            if (!IsAppWindow(h) || Text(h) == "") continue;
            if (!GetWindowRect(h, out var r) || r.Width <= 0 || r.Height <= 0 || !Contains(h)) continue; // 大きさの無い窓(隠れた持ち主役)は除く
            var rank = (ClassName(h) == className ? 4 : 0) + (GetWindow(h, GW_OWNER) == 0 ? 2 : 0) + (IsIconic(h) ? 0 : 1);
            if (rank > bestRank) (best, bestRank) = (h, rank);
        }
        return best;
    }

    /// <summary>
    /// 録画の基準にするウィンドウ。current が隠れた・閉じた(タブの切り替え)ら見えているものへ、
    /// 別プロセスの同じ種類のウィンドウが前面に来たらそれへ移る。同じプロセスのダイアログなどには移らない。
    /// </summary>
    public nint Follow(nint current, string className, nint foreground)
    {
        if (foreground != current && IsShown(foreground) && !IsIconic(foreground) && ProcessId(foreground) != ProcessId(current)
            && ClassName(foreground) == className && Contains(foreground)) return foreground;
        var shown = IsShown(current);
        if (shown && !IsIconic(current)) return current;
        var next = MainWindow(className: className);
        return next == 0 || shown && IsIconic(next) ? current : next;
    }

    static bool IsShown(nint window) => window != 0 && IsWindowVisible(window) && !IsCloaked(window);

    /// <summary>
    /// 利用者がアプリとして相手にするウィンドウか。見えていて(cloak もされていない)、ツールウィンドウでない。
    /// 常駐の隠れた窓(TextInputHost・PowerToys.QuickAccess など)や、透明なオーバーレイを除く。
    /// </summary>
    public static bool IsAppWindow(nint window) => IsShown(window) && ((long)GetWindowLongPtr(window, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) == 0;
}
