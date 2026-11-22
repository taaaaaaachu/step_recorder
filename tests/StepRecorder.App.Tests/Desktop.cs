using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Automation;

// E2E テストはデスクトップ(マウス・キーボード・前面のウィンドウ)を取り合うので、並列に走らせない
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace StepRecorder.App.Tests;

/// <summary>実際のデスクトップでマウスとキーボードを動かすので、STEP_RECORDER_E2E=1 のときだけ走る。</summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (!Desktop.InputAllowed) Skip = "STEP_RECORDER_E2E=1 で有効になる(実行中はマウスとキーボードが動く)";
    }
}

/// <summary>実際のデスクトップにウィンドウは出すが、マウスとキーボードは動かさない。STEP_RECORDER_DESKTOP=1 でも走る。</summary>
public sealed class DesktopFactAttribute : FactAttribute
{
    public DesktopFactAttribute()
    {
        if (!Desktop.InputAllowed && Environment.GetEnvironmentVariable("STEP_RECORDER_DESKTOP") != "1")
            Skip = "STEP_RECORDER_DESKTOP=1 で有効になる(実行中はウィンドウが開く。入力は送らない)";
    }
}

/// <summary>E2E テスト用のデスクトップ操作。入力は対象が前面・最上位にあると確かめてから送る(他のアプリに入力しないため)。</summary>
static class Desktop
{
    [ModuleInitializer]
    internal static void Init() => SetProcessDpiAwarenessContext(-4); // PER_MONITOR_AWARE_V2。アプリ本体と座標系を揃える

    public static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "StepRecorder.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("リポジトリ直下が見つからない");
    }

    /// <summary>テストと同じ構成(Debug/Release)でビルドしたテスト用アプリ。</summary>
    public static string SampleExe =>
        Path.Combine(RepoRoot(), "tests", "SampleTarget", "bin",
            Path.GetFileName(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)))!, "net10.0-windows", "SampleTarget.exe");

    /// <summary>マウスとキーボードを動かしてよいか。</summary>
    public static bool InputAllowed => Environment.GetEnvironmentVariable("STEP_RECORDER_E2E") == "1";

    static void RequireInput()
    {
        if (!InputAllowed) throw new InvalidOperationException("入力を送るテストは E2EFact にする");
    }

    /// <summary>
    /// テストから起動するアプリの USERPROFILE。step_recorder は設定(settings.json)を保存するので、
    /// 利用者の設定を書き換えないよう、テストの間だけの一時フォルダに逃がす(自動更新の確認もしない)。
    /// </summary>
    public static readonly string Profile = CreateProfile();

    static string CreateProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), "sr-test-profile-" + Guid.NewGuid());
        foreach (var folder in new[] { @"AppData\Roaming", @"AppData\Local", "Documents" }) Directory.CreateDirectory(Path.Combine(root, folder));
        new StepRecorder.Core.Settings { AutoUpdate = false, OutputDir = Path.Combine(root, "records") }
            .Save(Path.Combine(root, @"AppData\Roaming\step_recorder\settings.json"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        };
        return root;
    }

    /// <summary>起動してメインウィンドウが出るのを待つ(入力は送らない)。設定は Profile に逃がす。</summary>
    public static Process Open(string exe, string args = "")
    {
        var start = new ProcessStartInfo(exe, args) { UseShellExecute = false };
        start.Environment["USERPROFILE"] = Profile;
        var p = Process.Start(start)!;
        for (var i = 0; i < 100 && p.MainWindowHandle == 0; i++) { Wait(100); p.Refresh(); }
        Assert.NotEqual(0, p.MainWindowHandle);
        return p;
    }

    /// <summary>起動してメインウィンドウが出るのを待ち、前面に出す。</summary>
    public static Process Launch(string exe, string args = "")
    {
        RequireInput();
        var p = Open(exe, args);
        // 裏のプロセスは前面を切り替えられないので、どのアプリも使わない F24 を1回送って入力の権利を得てから前面にする
        Send(Key(0x87), Key(0x87, KEYUP));
        SetForegroundWindow(p.MainWindowHandle);
        Wait(500);
        return p;
    }

    public static void Wait(int ms) => Thread.Sleep(ms);

    /// <summary>cond が成り立つまで待つ(最大 timeoutMs)。成り立ったかを返す。</summary>
    public static bool Until(Func<bool> cond, int timeoutMs = 5000)
    {
        var end = DateTime.Now.AddMilliseconds(timeoutMs);
        while (!cond()) { if (DateTime.Now > end) return false; Thread.Sleep(100); }
        return true;
    }

    /// <summary>起動直後は部品が UI Automation にまだ出ていないことがあるので、少し待ちながら探す。</summary>
    public static AutomationElement Find(nint hwnd, Condition condition)
    {
        AutomationElement? el = null;
        for (var i = 0; i < 30 && el == null; i++, Wait(100))
            el = AutomationElement.FromHandle(hwnd).FindFirst(TreeScope.Descendants, condition);
        Assert.NotNull(el);
        return el;
    }

    public static AutomationElement Find(nint hwnd, string automationId) =>
        Find(hwnd, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

    public static string ValueOf(nint hwnd, string automationId) =>
        ((ValuePattern)Find(hwnd, automationId).GetCurrentPattern(ValuePattern.Pattern)).Current.Value;

    public static void ClickOn(nint hwnd, string automationId) =>
        ClickOn(hwnd, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

    public static void ClickOn(nint hwnd, Condition condition)
    {
        var r = Find(hwnd, condition).Current.BoundingRectangle;
        Click(hwnd, (int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
    }

    /// <summary>クリックする。クリック位置に別のウィンドウが重なっていたら中止する。</summary>
    public static void Click(nint hwnd, int x, int y)
    {
        var pt = new Native.POINT { X = x, Y = y };
        if (Native.ProcessId(Native.WindowFromPoint(pt)) != Native.ProcessId(hwnd))
            throw new InvalidOperationException("クリック位置に別のウィンドウが重なっているので中止した");
        SetCursor(pt.X, pt.Y);
        Send(Mouse(0x0002), Mouse(0x0004)); // LEFTDOWN, LEFTUP
    }

    /// <summary>カーソルを (x, y) へ動かす(クリックはしない)。</summary>
    public static void MoveCursor(int x, int y) => SetCursor(x, y);

    /// <summary>ホイールボタン(中ボタン)を押したまま (x1, y1) から (x2, y2) へドラッグする。</summary>
    public static void MiddleDrag(nint hwnd, int x1, int y1, int x2, int y2)
    {
        var pt = new Native.POINT { X = x1, Y = y1 };
        if (Native.ProcessId(Native.WindowFromPoint(pt)) != Native.ProcessId(hwnd))
            throw new InvalidOperationException("ドラッグ位置に別のウィンドウが重なっているので中止した");
        SetCursor(x1, y1);
        Send(Mouse(0x0020)); // MIDDLEDOWN
        for (var i = 1; i <= 10; i++)
        {
            SetCursor(x1 + (x2 - x1) * i / 10, y1 + (y2 - y1) * i / 10);
            Wait(20);
        }
        Send(Mouse(0x0040)); // MIDDLEUP
    }

    /// <summary>(x, y) で Ctrl を押しながらホイールを回す。notches が正なら奥へ。</summary>
    public static void CtrlWheel(nint hwnd, int x, int y, int notches)
    {
        var pt = new Native.POINT { X = x, Y = y };
        if (Native.ProcessId(Native.WindowFromPoint(pt)) != Native.ProcessId(hwnd))
            throw new InvalidOperationException("ホイール位置に別のウィンドウが重なっているので中止した");
        EnsureForeground(hwnd);
        SetCursor(x, y);
        Send(Key(0x11), new INPUT { type = 0, u = new() { mi = new() { dwFlags = 0x0800, mouseData = unchecked((uint)(notches * 120)) } } }, Key(0x11, KEYUP));
    }

    /// <summary>キーを順に押してから逆順に離す(Ctrl+Tab なら 0x11, 0x09)。</summary>
    public static void PressKeys(nint hwnd, params ushort[] vks)
    {
        EnsureForeground(hwnd);
        Send([.. vks.Select(vk => Key(vk)), .. vks.Reverse().Select(vk => Key(vk, KEYUP))]);
    }

    public static void TypeText(nint hwnd, string text)
    {
        EnsureForeground(hwnd);
        foreach (var c in text) Send(Char(c, 0), Char(c, KEYUP));
    }

    static void EnsureForeground(nint hwnd)
    {
        if (Native.ProcessId(Native.GetForegroundWindow()) != Native.ProcessId(hwnd))
            throw new InvalidOperationException("対象のアプリが前面にないのでキー入力を送らずに中止した");
    }

    const uint KEYUP = 0x0002, UNICODE = 0x0004;

    static void Send(params INPUT[] inputs)
    {
        RequireInput();
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    static void SetCursor(int x, int y)
    {
        RequireInput();
        SetCursorPos(x, y);
    }
    static INPUT Mouse(uint flags) => new() { type = 0, u = new() { mi = new() { dwFlags = flags } } };
    static INPUT Key(ushort vk, uint flags = 0) => new() { type = 1, u = new() { ki = new() { wVk = vk, dwFlags = flags } } };
    static INPUT Char(char c, uint flags) => new() { type = 1, u = new() { ki = new() { wScan = c, dwFlags = UNICODE | flags } } };

    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }

    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(nint value);
}
