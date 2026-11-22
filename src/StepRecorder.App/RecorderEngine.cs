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
    readonly bool captureCursor;
    readonly bool recordVideo;
    readonly HashSet<(HotkeyModifiers, int)> ownHotkeys = []; // 記録用のホットキー自体は記録しない
    readonly Recording rec;
    readonly BlockingCollection<Action> queue = new();
    readonly Thread worker, hookThread;
    readonly ManualResetEventSlim hookReady = new();
    uint hookThreadId;

    // フックの delegate は GC されないようフィールドで持つ
    readonly HookProc mouseProc, keyProc;

    // ワーカー専用の状態
    ClickEvent? lastClick;
    Step? lastClickStep;
    AutomationElement? typing;
    nint lastTargetRoot;
    readonly Timer flushTimer;

    // 動画(ワーカー専用)。clip はホットキーで録っている途中の動画のステップ、full は録画モードの全編
    (VideoRecorder Recorder, Step Step)? clip;
    VideoRecorder? full;

    public volatile RecordMode Mode;
    public volatile bool Paused;

    /// <summary>ステップ数が変わった。ワーカースレッドから呼ばれる。</summary>
    public event Action<int>? StepCountChanged;

    /// <summary>動画のステップを録り始めた(true)・録り終えた(false)。ワーカースレッドから呼ばれる。</summary>
    public event Action<bool>? ClipChanged;

    public RecorderEngine(Process target, string dir, Settings settings)
    {
        pid = (uint)target.Id;
        processName = target.ProcessName;
        this.dir = dir;
        captureCursor = settings.CaptureCursor;
        recordVideo = settings.DefaultMode == RecordMode.Video;
        Mode = settings.DefaultMode;
        foreach (var text in new[] { settings.HotkeyCapture, settings.HotkeyPause, settings.HotkeyStop, settings.HotkeyClip })
            if (HotkeyInput.TryParse(text, out var mods, out var vk)) ownHotkeys.Add((mods, vk));
        lastTargetRoot = target.MainWindowHandle;
        rec = new Recording(new Session { CreatedAt = DateTimeOffset.Now, ProcessName = processName, Evidence = EvidenceCollector.Collect(target) });
        mouseProc = MouseProc;
        keyProc = KeyProc;
        flushTimer = new Timer(_ => Enqueue(Flush));
        worker = new Thread(Work) { IsBackground = true, Name = "recorder-worker" };
        worker.SetApartmentState(ApartmentState.STA); // WPF の画像処理用
        hookThread = new Thread(HookLoop) { IsBackground = true, Name = "recorder-hooks" };
    }

    public void Start()
    {
        worker.Start();
        if (recordVideo) Enqueue(StartFullVideo);
        hookThread.Start();
        hookReady.Wait();
    }

    public void CaptureManual() => Enqueue(() =>
    {
        Flush();
        if (clip != null) return;
        var root = TargetRoot();
        if (Capture(root) is { } shot) AddShot(CaptureTrigger.Manual, root, shot, null);
    });

    /// <summary>記録を止め、溜まった処理を終えてからセッションを返す。stage にはいま何をしているかを知らせる(ワーカースレッドからも呼ぶ)。</summary>
    public Session Stop(Action<string>? stage = null)
    {
        PostThreadMessage(hookThreadId, WM_QUIT, 0, 0);
        hookThread.Join();
        flushTimer.Dispose();
        stage?.Invoke($"撮った画像を保存しています(残り {queue.Count} 件)");
        Enqueue(() =>
        {
            Flush();
            if (clip != null || full != null) stage?.Invoke("動画を書き終えています");
            StopVideos();
        });
        queue.CompleteAdding();
        worker.Join();
        return rec.Session;
    }

    /// <summary>
    /// 動画のステップを録り始める。もう一度呼ぶと止めて、動画のステップを確定する。
    /// 録画モード(全編を録っている)では使えない(編集画面で全編から区間を切り出す)。
    /// </summary>
    public void ToggleClip() => Enqueue(() =>
    {
        if (recordVideo || Paused && clip == null) return;
        Flush();
        if (clip != null) { StopClip(); return; }
        // 操作しているウィンドウを録る。ステップは先に作り、録っている間の操作はこのステップに足す
        var root = TargetRoot();
        if (root == 0) return;
        var file = rec.NextVideoFile;
        var frame = FrameBounds(root);
        var step = rec.AddStep(CaptureTrigger.Video, new WindowInfo(Text(root), ClassName(root), processName), rec.NextImageFile,
            frame.Width & ~1, frame.Height & ~1, DateTimeOffset.Now);
        step.VideoFile = file;
        clip = (VideoRecorder.Start(root, Path.Combine(dir, file), captureCursor), step);
        lastTargetRoot = root;
        StepCountChanged?.Invoke(rec.Session.Steps.Count);
        ClipChanged?.Invoke(true);
    });

    void StopClip()
    {
        if (clip is not var (recorder, step)) return;
        clip = null;
        recorder.Stop();
        // ポスター(再生前に見せる画像)は動画の最初のフレーム
        var poster = VideoClips.Frame(Path.Combine(dir, step.VideoFile!), TimeSpan.Zero);
        WindowCapture.SavePng(poster, Path.Combine(dir, step.ImageFile));
        (step.ImageWidth, step.ImageHeight) = (poster.PixelWidth, poster.PixelHeight);
        ClipChanged?.Invoke(false);
    }

    const string FullVideoFile = "video.mp4";

    void StartFullVideo()
    {
        full = VideoRecorder.Start(lastTargetRoot, Path.Combine(dir, FullVideoFile), captureCursor);
        rec.Session.VideoFile = FullVideoFile;
    }

    void StopVideos()
    {
        StopClip();
        full?.Stop();
        full = null;
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
        hookReady.Set();
        while (GetMessage(out _, 0, 0, 0) > 0) { }
        UnhookWindowsHookEx(mouse);
        UnhookWindowsHookEx(key);
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
                Enqueue(() => { Flush(); if (mode != RecordMode.Manual) OnClick(root, ev); });
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

    // ---- ワーカースレッド ----

    void OnClick(nint root, ClickEvent ev)
    {
        if (clip != null)
        {
            // 動画を録っている間は撮らず、操作だけを動画のステップに足す(動画には強調枠を付けないので位置は持たない)
            var el = Uia.Info(Uia.FromPoint(ev.ScreenPoint.X, ev.ScreenPoint.Y), default);
            rec.AddOperation(new Operation { Kind = OperationKind.Click, Time = ev.Time, Button = ev.Button, Element = el == null ? null : el with { Rect = null } });
            return;
        }
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
        if (Capture(root) is not { } shot) return;
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
        var mods = (ctrl ? HotkeyModifiers.Ctrl : 0) | (alt ? HotkeyModifiers.Alt : 0) | (shift ? HotkeyModifiers.Shift : 0) | (win ? HotkeyModifiers.Win : 0);
        if (ownHotkeys.Contains((mods, vk))) return;
        var (kind, name) = KeyClassifier.Classify(vk, ctrl, alt, shift, win, ImeOpen(fg));
        if (kind == KeyKind.Shortcut)
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

    Shot? Capture(nint hwnd) => WindowCapture.Capture(hwnd, captureCursor);


    Step AddShot(CaptureTrigger trigger, nint root, Shot shot, Operation? op)
    {
        lastTargetRoot = root;
        var file = rec.NextImageFile;
        WindowCapture.SavePng(shot.Image, Path.Combine(dir, file));
        var step = rec.AddStep(trigger, new WindowInfo(Text(root), ClassName(root), processName), file,
            shot.Image.PixelWidth, shot.Image.PixelHeight, DateTimeOffset.Now, op);
        step.Crop = shot.Crop; // 画像はアプリ全体。ダイアログなどを操作したときは、そのウィンドウだけが見えるようにトリミングしておく
        step.VideoTime = full?.CurrentTime; // 録画モードなら、編集画面のタイムラインに目盛りを付ける
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
