using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using StepRecorder.Core;
using static StepRecorder.App.Native;

namespace StepRecorder.App;

/// <summary>
/// 記録中に最前面へ出す小さなバー。クリックしても前面を奪わない(対象アプリのフォーカスを保つ)。
/// 使っていない間は小さなタブに引っ込み、マウスを乗せると滑り出る。ピンで留めると引っ込まない。
/// </summary>
public partial class RecordBar : Window
{
    const int HkCapture = 1, HkPause = 2, HkStop = 3, HkClip = 4;

    readonly RecorderEngine engine;
    readonly Settings settings;
    readonly string dir;
    bool stopped;
    readonly System.Windows.Threading.DispatcherTimer clipTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Stopwatch clipClock = new(); // 一時停止の間は進めない(動画の長さに合わせる)
    readonly string recordingLabel;

    readonly Brush recordingTab = new SolidColorBrush(Color.FromRgb(0xE0, 0x40, 0x40));
    readonly AutoHide autoHide = new();
    readonly Stopwatch shownClock = new();
    readonly System.Windows.Threading.DispatcherTimer hideTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    static readonly TimeSpan OpenTime = TimeSpan.FromMilliseconds(220), CloseTime = TimeSpan.FromMilliseconds(180), TabTime = TimeSpan.FromMilliseconds(120);

    /// <summary>出ているか(滑っている間は、行き先の状態)。</summary>
    internal bool IsBarOpen { get; private set; } = true;

    /// <summary>滑らせるか。Windows の「アニメーション効果」を切っていれば、すぐに入れ替える。</summary>
    internal bool Animated { get; set; } = SystemParameters.ClientAreaAnimation;

    /// <summary>マウスが上にあるか。入力を送らないテストでは、利用者のカーソルに左右されないよう差し替える。</summary>
    internal Func<bool> IsPointerOver { get; set; }

    /// <summary>停止後のセッション。</summary>
    public Session? Result { get; private set; }

    /// <summary>停止の進み具合。編集画面を開き終えたら呼び出し側が Finish する。</summary>
    internal ProgressWindow? Progress { get; private set; }

    internal RecordBar(RecorderEngine engine, Settings settings, string dir)
    {
        InitializeComponent();
        this.engine = engine;
        this.settings = settings;
        this.dir = dir;
        (settings.DefaultMode == RecordMode.Manual ? ModeManual : ModeClick).IsChecked = true;
        engine.StepCountChanged += n => Dispatcher.BeginInvoke(() => Count.Text = $"{n} 枚");
        engine.ClipChanged += recording => Dispatcher.BeginInvoke(() => OnClipChanged(recording));
        clipTimer.Tick += (_, _) => ShowClipTime();
        if (settings.DefaultMode == RecordMode.Video)
        {
            // クリックごとに撮りつつ全編を録っている。区間は編集画面で切り出すので録画ボタンは使わない
            ModeGroup.Visibility = ClipButton.Visibility = Visibility.Collapsed;
            State.Text = "● 記録中(録画)";
        }
        recordingLabel = State.Text;
        Count.Text = $"{engine.StepCount} 枚"; // 続けて記録なら前の分から
        Loaded += (_, _) => CenterPanel();
        ContentRendered += (_, _) =>
        {
            shownClock.Start();
            hideTimer.Start();
        };
        hideTimer.Tick += (_, _) => UpdateAutoHide();
        IsPointerOver = IsCursorOver;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)((long)GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        if (engine.CapturesScreen) ExcludeFromCapture(hwnd);
        PlaceOnTargetMonitor(hwnd);

        var failed = new List<string>();
        var hotkeys = new List<(int, string)> { (HkCapture, settings.HotkeyCapture), (HkPause, settings.HotkeyPause), (HkStop, settings.HotkeyStop) };
        if (settings.DefaultMode != RecordMode.Video) hotkeys.Add((HkClip, settings.HotkeyClip));
        foreach (var (id, text) in hotkeys)
        {
            if (!HotkeyInput.TryParse(text, out var mods, out var vk) || !RegisterHotKey(hwnd, id, (uint)mods, (uint)vk))
                failed.Add(text);
        }
        if (failed.Count > 0)
            MessageBox.Show($"ホットキーを登録できませんでした(他のアプリが使用中か、キー名が不正です): {string.Join(", ", failed)}\nバーのボタンは使えます。",
                "step_recorder", MessageBoxButton.OK, MessageBoxImage.Warning);
        engine.Start();
    }

    /// <summary>
    /// 対象があるモニターの上端中央に置く(タブが画面の端にくっつくように)。見える前に物理ピクセルで置く。
    /// Left/Top は拡大率の違うモニターの間でずれる。移ると拡大率に合わせて大きさが変わるので、移してから幅を見て中央へ寄せる。
    /// </summary>
    void PlaceOnTargetMonitor(nint hwnd)
    {
        if (Monitors.Info(engine.MonitorHandle) is not { } info) return;
        var work = info.rcWork;
        SetWindowPos(hwnd, 0, work.Left, work.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        if (!GetWindowRect(hwnd, out var r)) return;
        SetWindowPos(hwnd, 0, work.Left + (work.Width - r.Width) / 2, work.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    nint WndProc(nint hwnd, int msg, nint w, nint l, ref bool handled)
    {
        if (msg != WM_HOTKEY) return 0;
        handled = true;
        switch ((int)w)
        {
            case HkCapture: Capture_Click(this, new()); break;
            case HkPause: Pause_Click(this, new()); break;
            case HkStop: Stop_Click(this, new()); break;
            case HkClip: Clip_Click(this, new()); break;
        }
        return 0;
    }

    // ---- 自動で引っ込める ----

    /// <summary>
    /// MouseEnter/MouseLeave は使わない。前面にならないウィンドウで、しかも乗った所で中身が動くので取りこぼす。
    /// カーソルの位置を見る。
    /// </summary>
    void UpdateAutoHide()
    {
        autoHide.Update(IsPointerOver() || !docked, Native.KeyDown(VK_LBUTTON) || Native.KeyDown(VK_RBUTTON), shownClock.Elapsed);
        ShowOpen(autoHide.IsOpen);
    }

    bool IsCursorOver()
    {
        var cursor = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!IsVisible || !GetCursorInfo(ref cursor)) return false;
        // 滑っている途中の位置ではなく、行き先の範囲で見る(動く端にカーソルがあっても行ったり来たりしない)
        var area = IsBarOpen
            ? new Rect(Panel.Margin.Left, 0, Panel.ActualWidth, Panel.ActualHeight)
            : new Rect((ActualWidth - BarTab.ActualWidth) / 2, 0, BarTab.ActualWidth, BarTab.ActualHeight);
        return area.Contains(PointFromScreen(new Point(cursor.ptScreenPos.X, cursor.ptScreenPos.Y)));
    }

    void BarTab_Click(object sender, RoutedEventArgs e)
    {
        autoHide.Open(shownClock.Elapsed);
        ShowOpen(true);
    }

    void Pin_Changed(object sender, RoutedEventArgs e) => autoHide.IsPinned = PinButton.IsChecked == true;

    /// <summary>引っ込むときはバーが上へ消えてからタブが下りる。出るときはタブが上がりながらバーが下りる。途中で逆にもなれる。</summary>
    void ShowOpen(bool open)
    {
        if (open == IsBarOpen) return;
        IsBarOpen = open;
        Panel.IsHitTestVisible = open; // 消えていく途中のボタンは押せない
        if (open)
        {
            if (!Panel.IsVisible) CenterPanel(); // 見えている間は横に動かさない
            Panel.Visibility = Visibility.Visible;
            Slide(PanelSlide, 0, OpenTime, EasingMode.EaseOut);
            Slide(TabSlide, -BarTab.ActualHeight, TabTime, EasingMode.EaseIn, done: () => { if (IsBarOpen) BarTab.Visibility = Visibility.Hidden; });
        }
        else
        {
            BarTab.Visibility = Visibility.Visible;
            Slide(PanelSlide, -Panel.ActualHeight, CloseTime, EasingMode.EaseIn, done: () => { if (!IsBarOpen) Panel.Visibility = Visibility.Hidden; });
            Slide(TabSlide, 0, TabTime, EasingMode.EaseOut, delay: CloseTime);
        }
    }

    /// <summary>
    /// バーを中央に置く。開く直前にだけ呼ぶ(出ている間に文言で幅が変わっても、押しているボタンが動かないように左端は据え置く)。
    /// </summary>
    void CenterPanel()
    {
        UpdateLayout();
        Panel.Margin = new Thickness(Math.Max(0, Math.Round((ActualWidth - Panel.ActualWidth) / 2)), 0, 0, 0);
    }

    /// <summary>今の位置から to へ滑らせる。</summary>
    void Slide(TranslateTransform slide, double to, TimeSpan time, EasingMode easing, TimeSpan delay = default, Action? done = null)
    {
        if (!Animated)
        {
            slide.BeginAnimation(TranslateTransform.YProperty, null);
            slide.Y = to;
            done?.Invoke();
            return;
        }
        var animation = new DoubleAnimation(to, time) { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = easing } };
        if (done != null) animation.Completed += (_, _) => done();
        slide.BeginAnimation(TranslateTransform.YProperty, animation);
    }

    /// <summary>引っ込んでいても状態が分かるように、タブの色と印を変える。</summary>
    void ShowTabState()
    {
        BarTab.Background = engine.Paused ? Brushes.DimGray : recordingTab;
        BarTab.Content = engine.Paused ? "❚❚" : clipTimer.IsEnabled ? "■" : "●";
    }

    void Bar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
        KeepOnScreen();
    }

    /// <summary>上端にくっついているか。離して置いたバーは引っ込めない(引っ込むと端にタブが無く、出せなくなる)。</summary>
    bool docked = true;

    /// <summary>動かしたバーを作業領域の内側へ戻す(めり込んだまま引っ込むとマウスが届かない)。上端の近くなら上端にくっつける。</summary>
    void KeepOnScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(hwnd, out var win)) return;
        var (tl, br) = (Panel.PointToScreen(new Point(0, 0)), Panel.PointToScreen(new Point(Panel.ActualWidth, Panel.ActualHeight)));
        var bar = new RECT { Left = (int)tl.X, Top = (int)tl.Y, Right = (int)br.X, Bottom = (int)br.Y };
        if (Monitors.Info(MonitorFromRect(ref bar, MONITOR_DEFAULTTONEAREST)) is not { } info) return;
        var from = bar.ToPixelRect();
        var to = Core.Geometry.KeepIn(from, info.rcWork.ToPixelRect(), (int)(16 * VisualTreeHelper.GetDpi(this).DpiScaleY));
        docked = to.Y == info.rcWork.Top;
        if (to != from) SetWindowPos(hwnd, 0, win.Left + to.X - from.X, win.Top + to.Y - from.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    void Mode_Checked(object sender, RoutedEventArgs e) =>
        engine.Mode = sender == ModeManual ? RecordMode.Manual : RecordMode.Click;

    void Capture_Click(object sender, RoutedEventArgs e) => engine.CaptureManual();

    void Clip_Click(object sender, RoutedEventArgs e) => engine.ToggleClip();

    void OnClipChanged(bool recording)
    {
        if (!recording)
        {
            clipTimer.Stop();
            (ClipButton.Content, ClipButton.Tag) = ("録画", RecordIcon);
            ShowTabState();
            return;
        }
        clipClock.Restart();
        if (engine.Paused) clipClock.Stop();
        clipTimer.Start();
        ShowClipTime();
        ShowTabState();
    }

    // ボタンのアイコン(Segoe Fluent Icons の字)
    const string RecordIcon = "\uE7C8", StopIcon = "\uE71A", PauseIcon = "\uE769", PlayIcon = "\uE768";

    void ShowClipTime() => (ClipButton.Content, ClipButton.Tag) = ($"録画停止 {TimeText.Seconds(clipClock.Elapsed)}", StopIcon);

    void Pause_Click(object sender, RoutedEventArgs e)
    {
        engine.Paused = !engine.Paused;
        if (engine.Paused) clipClock.Stop();
        else if (clipTimer.IsEnabled) clipClock.Start();
        (PauseButton.Content, PauseButton.Tag) = engine.Paused ? ("再開", PlayIcon) : ("一時停止", PauseIcon);
        State.Text = engine.Paused ? "‖ 一時停止" : recordingLabel;
        ShowTabState();
    }

    async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (stopped) return;
        stopped = true;
        var hwnd = new WindowInteropHelper(this).Handle;
        foreach (var id in new[] { HkCapture, HkPause, HkStop, HkClip }) UnregisterHotKey(hwnd, id);
        clipTimer.Stop();
        hideTimer.Stop();
        engine.Paused = true; // 進み具合のウィンドウを出す前に録るのをやめる(最後に写り込ませない)
        // 溜まった撮影や動画の書き終えに時間がかかることがあるので、裏で止めて進み具合を出す
        Hide();
        Progress = new ProgressWindow("記録を終えています", "記録を止めています...", null);
        if (engine.CapturesScreen) Progress.SourceInitialized += (s, _) => ExcludeFromCapture(new WindowInteropHelper((Window)s!).Handle);
        Progress.Show();
        var result = await Task.Run(() =>
        {
            var session = engine.Stop(text => Progress.Report(text));
            Progress.Report("記録を保存しています");
            if (session.HasContent) SessionStore.Save(session, dir); // 編集前の状態も残しておく
            return session;
        });
        Progress.Report("編集画面を開いています");
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle); // 編集画面を作る間は描けないので、先に文言を描かせる
        Result = result;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Alt+F4 などで閉じられても記録は止めて残す。止め終えてから閉じる
        if (Result == null)
        {
            e.Cancel = true;
            Stop_Click(this, new());
        }
        base.OnClosing(e);
    }
}
