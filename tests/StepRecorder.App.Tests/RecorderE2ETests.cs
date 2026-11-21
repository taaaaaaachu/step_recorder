using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using StepRecorder.App;
using StepRecorder.Core;

namespace StepRecorder.App.Tests;

/// <summary>実際のデスクトップでマウスとキーボードを動かすので、STEP_RECORDER_E2E=1 のときだけ走る。</summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("STEP_RECORDER_E2E") != "1")
            Skip = "STEP_RECORDER_E2E=1 で有効になる(実行中はマウスとキーボードが動く)";
    }
}

public sealed class RecorderE2ETests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "sr-e2e-" + Guid.NewGuid());
    Process? target;

    static RecorderE2ETests() => SetProcessDpiAwarenessContext(-4); // PER_MONITOR_AWARE_V2。アプリ本体と座標系を揃える

    public void Dispose()
    {
        try { target?.Kill(); } catch (InvalidOperationException) { }
        // STEP_RECORDER_E2E_OUT を指定すると、目で確かめられるよう出力を HTML 付きで残す
        if (Environment.GetEnvironmentVariable("STEP_RECORDER_E2E_OUT") is { Length: > 0 } outDir && File.Exists(Path.Combine(dir, SessionStore.FileName)))
        {
            Export.SaveAll(SessionStore.Load(dir), dir, GroupMode.Sequence);
            Directory.CreateDirectory(outDir);
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(outDir, Path.GetRelativePath(dir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(file, to, true);
            }
        }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    [E2EFact]
    public void 手動撮影_クリック_入力_画面切り替わりを記録できる()
    {
        target = StartSample();
        var main = target.MainWindowHandle;
        var engine = new RecorderEngine(target, dir, new Settings { DefaultMode = CaptureTrigger.Click, WindowChangeDelayMs = 300 });
        engine.Start();

        engine.CaptureManual();
        Wait(500);

        ClickOn(main, "nameBox");
        Wait(500);
        TypeText(main, "abc");
        Wait(300);
        var box = AutomationElement.FromHandle(main).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "nameBox"));
        Assert.Equal("abc", ((ValuePattern)box.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
        Assert.Equal("nameBox", AutomationElement.FocusedElement.Current.AutomationId);

        engine.Mode = CaptureTrigger.WindowChange; // OK のクリックは撮らず、出てきたダイアログを撮る
        ClickOn(main, "okButton");
        Wait(1500);

        var session = engine.Stop();
        var log = Path.Combine(dir, "error.log");
        Assert.False(File.Exists(log), File.Exists(log) ? File.ReadAllText(log) : "");
        SessionStore.Save(session, dir);
        Assert.Equal("SampleTarget", session.ProcessName);
        Assert.True(session.Steps.Count >= 3, $"ステップ数 {session.Steps.Count}");

        var manual = session.Steps[0];
        Assert.Equal(CaptureTrigger.Manual, manual.Trigger);
        Assert.Equal("サンプル画面", manual.ScreenName);

        var click = session.Steps[1];
        Assert.Equal(CaptureTrigger.Click, click.Trigger);
        Assert.Equal("『氏名』欄をクリック\n『氏名』欄に「abc」と入力", click.Description);
        Assert.Single(click.Highlights);

        var dialog = session.Steps[2];
        Assert.Equal(CaptureTrigger.WindowChange, dialog.Trigger);
        Assert.Equal("『確認』画面が表示された", dialog.Description);

        foreach (var s in session.Steps)
        {
            var info = new FileInfo(Path.Combine(dir, s.ImageFile));
            Assert.True(info.Exists && info.Length > 1000, $"{s.ImageFile} が撮れていない");
            Assert.True(s.ImageWidth > 100 && s.ImageHeight > 50);
        }
    }

    static Process StartSample()
    {
        // テストと同じ構成(Debug/Release)の出力を使う
        var config = Path.GetFileName(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd('\\')))!;
        var exe = Path.Combine(RepoRoot(), "tests", "SampleTarget", "bin", config, "net10.0-windows", "SampleTarget.exe");
        var p = Process.Start(exe)!;
        for (var i = 0; i < 100 && p.MainWindowHandle == 0; i++) { Thread.Sleep(100); p.Refresh(); }
        Assert.NotEqual(0, p.MainWindowHandle);
        // 裏のプロセスは前面を切り替えられないので、どのアプリも使わない F24 を1回送って入力の権利を得てから前面にする
        Send(Key(0x87), Key(0x87, 0x0002));
        SetForegroundWindow(p.MainWindowHandle);
        Wait(500);
        return p;
    }

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "StepRecorder.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("リポジトリ直下が見つからない");
    }

    static void Wait(int ms) => Thread.Sleep(ms);

    /// <summary>キー入力の前に呼ぶ。対象が前面でなければ送らずに失敗させる(他のアプリに入力しないため)。</summary>
    static void EnsureForeground(nint hwnd)
    {
        if (Native.ProcessId(Native.GetForegroundWindow()) != Native.ProcessId(hwnd))
            throw new InvalidOperationException("テスト用アプリが前面にないのでキー入力を送らずに中止した");
    }

    /// <summary>部品の中央をクリックする。テストプロセスからは前面を切り替えられないので、クリックで前面にする。</summary>
    static void ClickOn(nint hwnd, string automationId)
    {
        var el = AutomationElement.FromHandle(hwnd).FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        Assert.NotNull(el);
        var r = el.Current.BoundingRectangle;
        var pt = new Native.POINT { X = (int)(r.X + r.Width / 2), Y = (int)(r.Y + r.Height / 2) };
        if (Native.ProcessId(Native.WindowFromPoint(pt)) != Native.ProcessId(hwnd))
            throw new InvalidOperationException($"クリック位置に別のウィンドウが重なっているので中止した: pt=({pt.X},{pt.Y}) hit={Native.ClassName(Native.WindowFromPoint(pt))}/{Native.Text(Native.GetAncestor(Native.WindowFromPoint(pt), Native.GA_ROOT))} pid={Native.ProcessId(Native.WindowFromPoint(pt))} want={Native.ProcessId(hwnd)} frame={Native.FrameBounds(hwnd).Left},{Native.FrameBounds(hwnd).Top},{Native.FrameBounds(hwnd).Right},{Native.FrameBounds(hwnd).Bottom} dpiAware={GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext())}");
        SetCursorPos(pt.X, pt.Y);
        Send(Mouse(0x0002), Mouse(0x0004)); // LEFTDOWN, LEFTUP
    }

    static void TypeText(nint hwnd, string text)
    {
        EnsureForeground(hwnd);
        foreach (var c in text) Send(Key(c, 0x0004), Key(c, 0x0004 | 0x0002)); // UNICODE, UNICODE|KEYUP
    }

    static void Send(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    static INPUT Mouse(uint flags) => new() { type = 0, u = new() { mi = new() { dwFlags = flags } } };

    static INPUT Key(char c, uint flags) => new() { type = 1, u = new() { ki = new() { wScan = c, dwFlags = flags } } };

    static INPUT Key(ushort vk, uint flags = 0) => new() { type = 1, u = new() { ki = new() { wVk = vk, dwFlags = flags } } };

    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }

    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint hwnd);


    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(nint value);
    [DllImport("user32.dll")] static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] static extern int GetAwarenessFromDpiAwarenessContext(nint c);
}
