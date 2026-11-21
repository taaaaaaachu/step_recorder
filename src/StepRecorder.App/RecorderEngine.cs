using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using StepRecorder.Core;
using static StepRecorder.App.Native;

namespace StepRecorder.App;

/// <summary>
/// 記録エンジン。フックはフック専用スレッドで受けてすぐ返し(返すのが遅いと Windows にフックを外される)、
/// 撮影・UI Automation・保存はすべてワーカースレッドで順番に処理する。Recording を触るのはワーカーだけ。
/// </summary>
sealed class RecorderEngine
{
    readonly uint pid;
    readonly string processName;
    readonly string dir;
    readonly int windowDelayMs;
    readonly HashSet<string> ownHotkeys;
    readonly Recording rec;
    readonly BlockingCollection<Action> queue = new();
    readonly Thread worker, hookThread;
    readonly ManualResetEventSlim hookReady = new();
    uint hookThreadId;

    // フックの delegate は GC されないようフィールドで持つ
    readonly HookProc mouseProc, keyProc;
    readonly WinEventProc winProc;

    // ワーカー専用の状態
    ClickEvent? lastClick;
    Step? lastClickStep;
    AutomationElement? typing;
    nint lastTargetRoot, lastShownHwnd;
    readonly Timer flushTimer;

    public volatile CaptureTrigger Mode;
    public volatile bool Paused;

    /// <summary>ステップ数が変わった。ワーカースレッドから呼ばれる。</summary>
    public event Action<int>? StepCountChanged;

    public RecorderEngine(Process target, string dir, Settings settings)
    {
        pid = (uint)target.Id;
        processName = target.ProcessName;
        this.dir = dir;
        windowDelayMs = settings.WindowChangeDelayMs;
        Mode = settings.DefaultMode;
        ownHotkeys = [settings.HotkeyCapture, settings.HotkeyPause, settings.HotkeyStop];
        lastTargetRoot = target.MainWindowHandle;
        rec = new Recording(new Session { CreatedAt = DateTimeOffset.Now, ProcessName = processName });
        mouseProc = MouseProc;
        keyProc = KeyProc;
        winProc = WinProc;
        flushTimer = new Timer(_ => Enqueue(Flush));
        worker = new Thread(Work) { IsBackground = true, Name = "recorder-worker" };
        worker.SetApartmentState(ApartmentState.STA); // WPF の画像処理用
        hookThread = new Thread(HookLoop) { IsBackground = true, Name = "recorder-hooks" };
    }

    public void Start()
    {
        worker.Start();
        hookThread.Start();
        hookReady.Wait();
    }

    public void CaptureManual() => Enqueue(() =>
    {
        Flush();
        var root = TargetRoot();
        if (WindowCapture.Capture(root) is { } shot) AddShot(CaptureTrigger.Manual, root, shot, null);
    });

    /// <summary>記録を止め、溜まった処理を終えてからセッションを返す。</summary>
    public Session Stop()
    {
        PostThreadMessage(hookThreadId, WM_QUIT, 0, 0);
        hookThread.Join();
        flushTimer.Dispose();
        Enqueue(Flush);
        queue.CompleteAdding();
        worker.Join();
        return rec.Session;
    }

    void Enqueue(Action a)
    {
        try { queue.Add(a); } catch (InvalidOperationException) { } // 停止後に届いたものは捨てる
    }

    void Work()
    {
        foreach (var a in queue.GetConsumingEnumerable())
        {
            try { a(); }
            catch (Exception e) { File.AppendAllText(Path.Combine(dir, "error.log"), $"{DateTimeOffset.Now:O} {e}\n"); }
        }
    }

    // ---- フックスレッド ----

    void HookLoop()
    {
        hookThreadId = GetCurrentThreadId();
        var mod = GetModuleHandle(null);
        var mouse = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, mod, 0);
        var key = SetWindowsHookEx(WH_KEYBOARD_LL, keyProc, mod, 0);
        var fg = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, winProc, pid, 0, WINEVENT_OUTOFCONTEXT);
        var show = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, 0, winProc, pid, 0, WINEVENT_OUTOFCONTEXT);
        hookReady.Set();
        while (GetMessage(out _, 0, 0, 0) > 0) { }
        UnhookWindowsHookEx(mouse);
        UnhookWindowsHookEx(key);
        UnhookWinEvent(fg);
        UnhookWinEvent(show);
    }

    nint MouseProc(int n, nint w, nint l)
    {
        if (n >= 0 && !Paused && (int)w is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(l);
            var root = GetAncestor(WindowFromPoint(info.pt), GA_ROOT);
            if (ProcessId(root) == pid)
            {
                var button = (int)w switch { WM_RBUTTONDOWN => MouseButton.Right, WM_MBUTTONDOWN => MouseButton.Middle, _ => MouseButton.Left };
                var ev = new ClickEvent(DateTimeOffset.Now, new(info.pt.X, info.pt.Y), button);
                var mode = Mode;
                Enqueue(() => { Flush(); if (mode == CaptureTrigger.Click) OnClick(root, ev); });
            }
        }
        return CallNextHookEx(0, n, w, l);
    }

    nint KeyProc(int n, nint w, nint l)
    {
        if (n >= 0 && !Paused && (int)w is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            var vk = (int)Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(l).vkCode;
            var fg = GetForegroundWindow();
            if (ProcessId(fg) == pid)
            {
                bool ctrl = KeyDown(VK_CONTROL), alt = KeyDown(VK_MENU), shift = KeyDown(VK_SHIFT), win = KeyDown(VK_LWIN) || KeyDown(VK_RWIN);
                var time = DateTimeOffset.Now;
                Enqueue(() => OnKey(fg, vk, ctrl, alt, shift, win, time));
            }
        }
        return CallNextHookEx(0, n, w, l);
    }

    void WinProc(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (Paused || Mode != CaptureTrigger.WindowChange || idObject != OBJID_WINDOW || idChild != 0) return;
        if (GetAncestor(hwnd, GA_ROOT) != hwnd || (GetWindowLongPtr(hwnd, GWL_STYLE) & WS_CAPTION) != WS_CAPTION) return;
        // 描画が終わるのを待ってから撮る
        Task.Delay(windowDelayMs).ContinueWith(_ => Enqueue(() => OnWindowShown(hwnd)));
    }

    // ---- ワーカースレッド ----

    void OnClick(nint root, ClickEvent ev)
    {
        if (lastClickStep != null && ClickRules.IsDoubleClick(lastClick, ev, (int)GetDoubleClickTime(),
                GetSystemMetrics(SM_CXDOUBLECLK), GetSystemMetrics(SM_CYDOUBLECLK)))
        {
            // 2回目の押下は新しいステップにせず、直前のクリックをダブルクリックにする
            var op = lastClickStep.Operations.LastOrDefault(o => o.Kind == OperationKind.Click);
            if (op != null) op.DoubleClick = true;
            lastClickStep.Description = Describer.Describe(lastClickStep.Operations);
            lastClick = null;
            return;
        }
        if (WindowCapture.Capture(root) is not { } shot) return;
        var b = shot.Bounds;
        var click = new Operation
        {
            Kind = OperationKind.Click,
            Time = ev.Time,
            Button = ev.Button,
            Point = new(ev.ScreenPoint.X - b.Left, ev.ScreenPoint.Y - b.Top),
            Element = Uia.Info(Uia.FromPoint(ev.ScreenPoint.X, ev.ScreenPoint.Y), b),
        };
        lastClickStep = AddShot(CaptureTrigger.Click, root, shot, click);
        lastClick = ev;
    }

    void OnKey(nint fg, int vk, bool ctrl, bool alt, bool shift, bool win, DateTimeOffset time)
    {
        var (kind, name) = KeyClassifier.Classify(vk, ctrl, alt, shift, win, ImeOpen(fg));
        if (kind == KeyKind.Shortcut && !ownHotkeys.Contains(name))
        {
            Flush();
            rec.AddOperation(new Operation { Kind = OperationKind.KeyPress, Keys = name, Time = time });
        }
        else if (kind == KeyKind.Typing)
        {
            typing ??= Uia.Focused();
            flushTimer.Change(1500, Timeout.Infinite); // 入力が止まったら確定させる
        }
    }

    /// <summary>入力中の欄の値を読み、入力操作として記録する。同じ欄への続きの入力は Recording が1つにまとめる。</summary>
    void Flush()
    {
        if (typing is not { } e) return;
        typing = null;
        var v = Uia.Value(e);
        if (v != null)
            rec.AddOperation(new Operation { Kind = OperationKind.TextInput, Time = DateTimeOffset.Now, Element = Uia.Info(e, default), Text = v });
    }

    void OnWindowShown(nint hwnd)
    {
        if (Paused || !IsWindowVisible(hwnd) || hwnd == lastShownHwnd) return; // 同じウィンドウに戻っただけなら撮らない
        Flush();
        if (WindowCapture.Capture(hwnd) is not { } shot) return;
        lastShownHwnd = hwnd;
        var op = new Operation { Kind = OperationKind.WindowShown, Time = DateTimeOffset.Now, Element = new(Text(hwnd), "Window", "", null) };
        AddShot(CaptureTrigger.WindowChange, hwnd, shot, op);
    }

    Step AddShot(CaptureTrigger trigger, nint root, Shot shot, Operation? op)
    {
        lastTargetRoot = root;
        var file = rec.NextImageFile;
        WindowCapture.SavePng(shot.Image, Path.Combine(dir, file));
        var step = rec.AddStep(trigger, new WindowInfo(Text(root), ClassName(root), processName), file,
            shot.Image.PixelWidth, shot.Image.PixelHeight, DateTimeOffset.Now, op);
        StepCountChanged?.Invoke(rec.Session.Steps.Count);
        return step;
    }

    /// <summary>手動撮影の対象。前面が対象プロセスならそれ、違えば最後に撮ったウィンドウ。</summary>
    nint TargetRoot()
    {
        var fg = GetAncestor(GetForegroundWindow(), GA_ROOT);
        if (ProcessId(fg) == pid) return fg;
        if (IsWindow(lastTargetRoot) && IsWindowVisible(lastTargetRoot)) return lastTargetRoot;
        try { return Process.GetProcessById((int)pid).MainWindowHandle; } catch (ArgumentException) { return 0; }
    }
}
