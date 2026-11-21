using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using StepRecorder.Core;
using static StepRecorder.App.Native;

namespace StepRecorder.App;

/// <summary>記録中に最前面へ出す小さなバー。クリックしても前面を奪わない(対象アプリのフォーカスを保つ)。</summary>
public partial class RecordBar : Window
{
    const int HkCapture = 1, HkPause = 2, HkStop = 3;

    readonly RecorderEngine engine;
    readonly Settings settings;
    readonly string dir;
    bool stopped;

    /// <summary>停止後のセッション。</summary>
    public Session? Result { get; private set; }

    internal RecordBar(RecorderEngine engine, Settings settings, string dir)
    {
        InitializeComponent();
        this.engine = engine;
        this.settings = settings;
        this.dir = dir;
        (engine.Mode switch { CaptureTrigger.Manual => ModeManual, CaptureTrigger.WindowChange => ModeWindow, _ => ModeClick }).IsChecked = true;
        engine.StepCountChanged += n => Dispatcher.BeginInvoke(() => Count.Text = $"{n} 枚");
        var area = SystemParameters.WorkArea;
        Left = area.Left + area.Width / 2 - 220;
        Top = area.Top + 4;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)((long)GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);

        var failed = new List<string>();
        foreach (var (id, text) in new[] { (HkCapture, settings.HotkeyCapture), (HkPause, settings.HotkeyPause), (HkStop, settings.HotkeyStop) })
        {
            if (!Hotkey.TryParse(text, out var hk) || !Enum.TryParse<Key>(hk.Key, true, out var key)
                || !RegisterHotKey(hwnd, id, (uint)hk.Modifiers, (uint)KeyInterop.VirtualKeyFromKey(key)))
                failed.Add(text);
        }
        if (failed.Count > 0)
            MessageBox.Show($"ホットキーを登録できませんでした(他のアプリが使用中か、キー名が不正です): {string.Join(", ", failed)}\nバーのボタンは使えます。",
                "step_recorder", MessageBoxButton.OK, MessageBoxImage.Warning);
        engine.Start();
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
        }
        return 0;
    }

    void Bar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    void Mode_Checked(object sender, RoutedEventArgs e) =>
        engine.Mode = sender == ModeManual ? CaptureTrigger.Manual : sender == ModeWindow ? CaptureTrigger.WindowChange : CaptureTrigger.Click;

    void Capture_Click(object sender, RoutedEventArgs e) => engine.CaptureManual();

    void Pause_Click(object sender, RoutedEventArgs e)
    {
        engine.Paused = !engine.Paused;
        PauseButton.Content = engine.Paused ? "再開" : "一時停止";
        State.Text = engine.Paused ? "‖ 一時停止" : "● 記録中";
    }

    void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (stopped) return;
        stopped = true;
        var hwnd = new WindowInteropHelper(this).Handle;
        foreach (var id in new[] { HkCapture, HkPause, HkStop }) UnregisterHotKey(hwnd, id);
        Result = engine.Stop();
        if (Result.Steps.Count > 0) SessionStore.Save(Result, dir); // 編集前の状態も残しておく
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!stopped) Stop_Click(this, new()); // Alt+F4 などで閉じられても記録は止めて残す
        base.OnClosing(e);
    }
}
