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
    readonly CaptureTarget target;
    readonly string dir;
    readonly bool captureCursor;
    readonly int fps;
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
    long typingPressed; // typing の欄で打ち始めたときの Stopwatch の時刻
    (AutomationElement? Element, bool Password) passwordField; // 1 打鍵ごとに UI Automation に問い合わせないよう、欄ごとに覚える
    nint lastTargetRoot;
    readonly Timer flushTimer;
    (MouseStroke Stroke, VideoRecorder Recorder)? held; // ボタンを押している間の、押したときの操作と、それを残した動画

    // フックスレッド専用。ボタンを押している間の動きを、DragInterval ごとにワーカーへ送る
    bool holding;
    long lastDragSent;
    static readonly TimeSpan DragInterval = TimeSpan.FromSeconds(1.0 / 60);
    const int DragThreshold = 4; // これより動かなければドラッグにしない(押したときの手ぶれ)

    // 動画(ワーカー専用)。clip はホットキーで録っている途中の動画のステップ、full は録画モードの全編
    (VideoRecorder Recorder, Step Step)? clip;
    VideoRecorder? full;

    public volatile RecordMode Mode;
    volatile bool paused;

    /// <summary>一時停止。操作も動画も録らない。動画に遅れずに効くよう、録画のスレッドが直接見る。</summary>
    public bool Paused
    {
        get => paused;
        set
        {
            paused = value;
            if (value) Enqueue(Flush); // 止める前に打った分までで確定する(停止中に打った分を読まない)
        }
    }

    /// <summary>ステップ数が変わった。ワーカースレッドから呼ばれる。</summary>
    public event Action<int>? StepCountChanged;

    /// <summary>動画のステップを録り始めた(true)・録り終えた(false)。ワーカースレッドから呼ばれる。</summary>
    public event Action<bool>? ClipChanged;

    public RecorderEngine(CaptureTarget target, string dir, Settings settings)
    {
        this.target = target;
        this.dir = dir;
        captureCursor = settings.CaptureCursor;
        fps = FrameRate.Valid(settings.Fps);
        recordVideo = settings.DefaultMode == RecordMode.Video;
        Mode = settings.DefaultMode;
        foreach (var text in new[] { settings.HotkeyCapture, settings.HotkeyPause, settings.HotkeyStop, settings.HotkeyClip })
            if (HotkeyInput.TryParse(text, out var mods, out var vk)) ownHotkeys.Add((mods, vk));
        lastTargetRoot = target.InitialWindow;
        rec = new Recording(new Session { CreatedAt = DateTimeOffset.Now, TargetName = target.Name, Evidence = target.CollectEvidence() });
        mouseProc = MouseProc;
        keyProc = KeyProc;
        flushTimer = new Timer(_ => Enqueue(Flush));
        worker = new Thread(Work) { IsBackground = true, Name = "recorder-worker" };
        worker.SetApartmentState(ApartmentState.STA); // WPF の画像処理用
        hookThread = new Thread(HookLoop) { IsBackground = true, Name = "recorder-hooks" };
    }

    /// <summary>画面に見えているものを撮るか(自分のウィンドウを写らないようにする必要がある)。</summary>
    public bool CapturesScreen => target.CapturesScreen;

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
        if (clip != null || !target.TryGetActiveWindow(lastTargetRoot, out var root)) return;
        if (target.Capture(root, captureCursor) is { } shot) AddShot(CaptureTrigger.Manual, root, shot, null);
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
        // ステップは録り始めたら作り、録っている間の操作はこのステップに足す
        if (!target.TryGetActiveWindow(lastTargetRoot, out var root)) return;
        var file = rec.NextVideoFile;
        var recorder = StartVideo(root, file);
        var step = rec.AddStep(CaptureTrigger.Video, target.Describe(root), rec.NextImageFile,
            recorder.Width, recorder.Height, DateTimeOffset.Now);
        step.VideoFile = file;
        clip = (recorder, step);
        lastTargetRoot = root;
        StepCountChanged?.Invoke(rec.Session.Steps.Count);
        ClipChanged?.Invoke(true);
    });

    void StopClip()
    {
        if (clip is not var (recorder, step)) return;
        clip = null;
        try
        {
            recorder.Stop();
            // ポスター(再生前に見せる画像)は動画の最初のフレーム
            var poster = VideoClips.Frame(Path.Combine(dir, step.VideoFile!), TimeSpan.Zero);
            WindowCapture.SavePng(poster, Path.Combine(dir, step.ImageFile));
            (step.ImageWidth, step.ImageHeight) = (poster.PixelWidth, poster.PixelHeight);
        }
        catch
        {
            rec.Session.Steps.Remove(step); // 動画もポスターも無いステップは残さない
            StepCountChanged?.Invoke(rec.Session.Steps.Count);
            throw;
        }
        finally { ClipChanged?.Invoke(false); }
    }

    const string FullVideoFile = "video.mp4";

    void StartFullVideo()
    {
        full = StartVideo(lastTargetRoot, FullVideoFile);
    }

    /// <summary>録画を始める。音を録れなければ映像だけにし、理由を error.log に残す。</summary>
    VideoRecorder StartVideo(nint root, string file)
    {
        var recorder = target.StartVideo(root, Path.Combine(dir, file), captureCursor, () => paused, fps);
        if (recorder.AudioFailure is { } e) Log(new InvalidOperationException($"{file} の音を録れませんでした", e));
        return recorder;
    }

    void Log(Exception e) => File.AppendAllText(Path.Combine(dir, "error.log"), $"{DateTimeOffset.Now:O} {e}\n");

    void StopVideos()
    {
        try { StopClip(); }
        finally { StopFullVideo(); }
    }

    void StopFullVideo()
    {
        if (full is not { } recorder) return;
        full = null;
        recorder.Stop();
        rec.Session.VideoFile = FullVideoFile; // 書き終えられたときだけ記録に載せる
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
            catch (Exception e) { Log(e); }
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
        if (n >= 0 && holding && (int)w is WM_MOUSEMOVE or WM_LBUTTONUP or WM_RBUTTONUP or WM_MBUTTONUP)
        {
            var up = (int)w != WM_MOUSEMOVE;
            if (up) holding = false;
            var now = Stopwatch.GetTimestamp();
            if (!Paused && (up || Stopwatch.GetElapsedTime(lastDragSent, now) >= DragInterval))
            {
                lastDragSent = now;
                var pt = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(l).pt;
                Enqueue(() => RecordDrag(pt, now, up));
            }
        }
        if (n >= 0 && !Paused && (int)w is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_MOUSEWHEEL or WM_MOUSEHWHEEL or WM_XBUTTONDOWN)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(l);
            var root = GetAncestor(WindowFromPoint(info.pt), GA_ROOT);
            if (target.OwnsClick(root, info.pt))
            {
                var pressed = Stopwatch.GetTimestamp();
                var up = (short)(info.mouseData >> 16) > 0; // ホイールの回した向き(横のホイールは正が右)
                switch ((int)w)
                {
                    case WM_MOUSEWHEEL: Enqueue(() => RecordMouse(info.pt, up ? MouseAction.WheelUp : MouseAction.WheelDown, pressed)); break;
                    case WM_MOUSEHWHEEL: Enqueue(() => RecordMouse(info.pt, up ? MouseAction.WheelRight : MouseAction.WheelLeft, pressed)); break;
                    case WM_XBUTTONDOWN:
                        // マウスの横の戻る・進むボタン。画面は撮らず、Alt+← などのキー操作と同じく操作として足す
                        var (side, name) = (info.mouseData >> 16) == 1 ? (MouseAction.Back, "マウスの戻るボタン") : (MouseAction.Forward, "マウスの進むボタン");
                        var at = DateTimeOffset.Now;
                        Enqueue(() =>
                        {
                            Flush();
                            RecordMouse(info.pt, side, pressed);
                            AddOperation(new Operation { Kind = OperationKind.KeyPress, Keys = name, Time = at }, pressed);
                        });
                        break;
                    default:
                        var (button, action) = (int)w switch
                        {
                            WM_RBUTTONDOWN => (MouseButton.Right, MouseAction.Right),
                            WM_MBUTTONDOWN => (MouseButton.Middle, MouseAction.Middle),
                            _ => (MouseButton.Left, MouseAction.Left),
                        };
                        var ev = new ClickEvent(DateTimeOffset.Now, new(info.pt.X, info.pt.Y), button);
                        var mode = Mode;
                        holding = true;
                        Enqueue(() =>
                        {
                            Flush();
                            held = RecordMouse(info.pt, action, pressed);
                            if (mode != RecordMode.Manual) OnClick(root, ev, pressed);
                        });
                        break;
                }
            }
        }
        return CallNextHookEx(0, n, w, l);
    }

    nint KeyProc(int n, nint w, nint l)
    {
        if (n >= 0 && !Paused && (int)w is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            var key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(l);
            var vk = (int)key.vkCode;
            char? packet = vk == VK_PACKET ? (char)key.scanCode : null; // 文字だけを送り込む打鍵(SendInput の KEYEVENTF_UNICODE)は、文字が scanCode に入る
            var fg = GetForegroundWindow();
            if (target.OwnsKey(fg))
            {
                bool ctrl = KeyDown(VK_CONTROL), alt = KeyDown(VK_MENU), shift = KeyDown(VK_SHIFT), win = KeyDown(VK_LWIN) || KeyDown(VK_RWIN);
                var time = DateTimeOffset.Now;
                var pressed = Stopwatch.GetTimestamp(); // 動画上の時刻は、ワーカーで処理するまでの遅れを引いて求める
                Enqueue(() => OnKey(fg, vk, ctrl, alt, shift, win, time, pressed, packet));
            }
        }
        return CallNextHookEx(0, n, w, l);
    }

    // ---- ワーカースレッド ----

    void OnClick(nint root, ClickEvent ev, long pressed)
    {
        if (clip != null)
        {
            // 動画を録っている間は撮らず、操作だけを動画のステップに足す(動画には強調枠を付けないので位置は持たない)
            var el = Uia.Info(Uia.FromPoint(ev.ScreenPoint.X, ev.ScreenPoint.Y), default);
            AddOperation(new Operation { Kind = OperationKind.Click, Time = ev.Time, Button = ev.Button, Element = el == null ? null : el with { Rect = null } }, pressed);
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
        if (target.Capture(root, captureCursor) is not { } shot) return;
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

    void OnKey(nint fg, int vk, bool ctrl, bool alt, bool shift, bool win, DateTimeOffset time, long pressed, char? packet)
    {
        var mods = (ctrl ? HotkeyModifiers.Ctrl : 0) | (alt ? HotkeyModifiers.Alt : 0) | (shift ? HotkeyModifiers.Shift : 0) | (win ? HotkeyModifiers.Win : 0);
        if (ownHotkeys.Contains((mods, vk))) return;
        var (kind, name) = KeyClassifier.Classify(vk, ctrl, alt, shift, win, ImeOpen(fg));
        if (kind == KeyKind.Shortcut)
        {
            Flush();
            AddOperation(new Operation { Kind = OperationKind.KeyPress, Keys = name, Time = time }, pressed);
        }
        else if (kind == KeyKind.Typing)
        {
            if (typing == null)
            {
                if (!target.OwnsKey(GetForegroundWindow())) return; // 処理するまでに対象の外へ移っていたら、そこの欄は読まない
                typing = Uia.Focused();
                typingPressed = pressed;
            }
            flushTimer.Change(1500, Timeout.Infinite); // 入力が止まったら確定させる
        }
        RecordKey(fg, vk, ctrl, alt, shift, win, pressed, packet);
    }

    /// <summary>
    /// 録画している間に押したキーを、動画に重ねるために動画上の時刻と一緒に残す(録画ボタンの動画はそのステップに、全編は記録に)。
    /// パスワード欄で打った文字は伏せる。
    /// </summary>
    void RecordKey(nint fg, int vk, bool ctrl, bool alt, bool shift, bool win, long pressed, char? packet)
    {
        var recorder = clip?.Recorder ?? full;
        if (recorder == null || KeyLabel.Of(vk, ctrl, alt, shift, win, packet ?? TypedChar(vk, shift, fg)) is not { } label) return;
        var text = label.Text;
        if (label.IsChar && typing is { } field)
        {
            if (!ReferenceEquals(passwordField.Element, field)) passwordField = (field, Uia.IsPassword(field));
            if (passwordField.Password) text = "●";
        }
        var at = recorder.CurrentTime - Stopwatch.GetElapsedTime(pressed);
        var keys = clip is var (_, step) ? step.Keys ??= [] : rec.Session.Keys ??= [];
        keys.Add(new KeyStroke(at < TimeSpan.Zero ? TimeSpan.Zero : at, text, label.IsChar));
    }

    /// <summary>
    /// 録画している間のクリック・ホイールを、動画に重ねるために動画上の時刻と位置と一緒に残す(残し先はキーと同じ)。動画の外は残さない。
    /// 残したら、その操作と動画を返す。
    /// </summary>
    (MouseStroke, VideoRecorder)? RecordMouse(POINT point, MouseAction action, long pressed)
    {
        var recorder = clip?.Recorder ?? full;
        if (recorder?.ToVideo(point) is not { } p) return null;
        var at = recorder.CurrentTime - Stopwatch.GetElapsedTime(pressed);
        var list = clip is var (_, step) ? step.Mouse ??= [] : rec.Session.Mouse ??= [];
        var stroke = new MouseStroke(at < TimeSpan.Zero ? TimeSpan.Zero : at, p.X, p.Y, action);
        list.Add(stroke);
        return (stroke, recorder);
    }

    /// <summary>ボタンを押したまま動かした位置を、押したときの操作に足す(up なら離した位置)。少ししか動かしていなければ足さない。動画の外は足さない。</summary>
    void RecordDrag(POINT point, long at, bool up)
    {
        if (held is not var (stroke, recorder)) return;
        if (up) held = null;
        if (recorder != (clip?.Recorder ?? full) || recorder.ToVideo(point) is not { } p) return; // 押したときの動画をもう録っていない
        if (stroke.Drag == null && (up || Math.Abs(p.X - stroke.X) <= DragThreshold && Math.Abs(p.Y - stroke.Y) <= DragThreshold)) return;
        (stroke.Drag ??= []).Add(new MousePoint(recorder.CurrentTime - Stopwatch.GetElapsedTime(at), p.X, p.Y));
    }

    /// <summary>入力中の欄の値を読み、入力操作として記録する。同じ欄への続きの入力は Recording が1つにまとめる。</summary>
    void Flush()
    {
        if (typing is not { } e) return;
        typing = null;
        var v = Uia.Value(e);
        if (v != null)
            AddOperation(new Operation { Kind = OperationKind.TextInput, Time = DateTimeOffset.Now, Element = Uia.Info(e, default), Text = v }, typingPressed);
    }

    /// <summary>直近のステップに操作を足す。録画ボタンの動画の間なら、押した時刻(pressed)の動画上の時刻も持たせる(処理するまでの遅れを引く)。</summary>
    void AddOperation(Operation op, long pressed)
    {
        if (clip is var (recorder, _))
        {
            var at = recorder.CurrentTime - Stopwatch.GetElapsedTime(pressed);
            op.VideoTime = at < TimeSpan.Zero ? TimeSpan.Zero : at;
        }
        rec.AddOperation(op);
    }


    Step AddShot(CaptureTrigger trigger, nint root, Shot shot, Operation? op)
    {
        lastTargetRoot = root;
        var file = rec.NextImageFile;
        WindowCapture.SavePng(shot.Image, Path.Combine(dir, file));
        var step = rec.AddStep(trigger, target.Describe(root), file,
            shot.Image.PixelWidth, shot.Image.PixelHeight, DateTimeOffset.Now, op);
        step.Crop = shot.Crop; // 画像はアプリ全体。ダイアログなどを操作したときは、そのウィンドウだけが見えるようにトリミングしておく
        step.VideoTime = full?.CurrentTime; // 録画モードなら、編集画面のタイムラインに目盛りを付ける
        StepCountChanged?.Invoke(rec.Session.Steps.Count);
        return step;
    }
}
