using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using StepRecorder.Core;

namespace StepRecorder.App;

public partial class MainWindow : Window
{
    sealed record ProcessItem(Process Process, string Label);

    readonly Settings settings = Settings.Load(Settings.DefaultPath);

    public MainWindow()
    {
        InitializeComponent();
        Version.Text = $"バージョン {AppVersion.Current}";
        ShowSettings();
        RefreshProcesses();
        Loaded += async (_, _) =>
        {
            if (settings.AutoUpdate) await Updater.CheckAsync(this);
        };
    }

    void ShowSettings()
    {
        (settings.DefaultMode switch
        {
            CaptureTrigger.Manual => ModeManual,
            CaptureTrigger.WindowChange => ModeWindow,
            _ => ModeClick,
        }).IsChecked = true;
        OutputDir.Text = settings.OutputDir;
        DrawWait.Text = settings.DrawWaitMs.ToString();
        HkCapture.Text = settings.HotkeyCapture;
        HkPause.Text = settings.HotkeyPause;
        HkStop.Text = settings.HotkeyStop;
        Group.SelectedIndex = (int)settings.GroupMode;
        CaptureCursor.IsChecked = settings.CaptureCursor;
        AutoUpdate.IsChecked = settings.AutoUpdate;
    }

    /// <summary>画面の入力を設定に反映して保存する。不正な値があればメッセージを出して false。</summary>
    bool ReadSettings()
    {
        if (!int.TryParse(DrawWait.Text, out var drawWait) || drawWait < 0) return Fail("描画待機時間は 0 以上の数値で入力してください");
        foreach (var (box, label) in new[] { (HkCapture, "手動撮影"), (HkPause, "一時停止"), (HkStop, "停止") })
            if (!Hotkey.TryParse(box.Text, out _)) return Fail($"{label}キーを設定してください(欄をクリックしてキーを押す)");
        if (new[] { HkCapture.Text, HkPause.Text, HkStop.Text }.Distinct().Count() < 3) return Fail("手動撮影・一時停止・停止には別々のキーを設定してください");
        if (string.IsNullOrWhiteSpace(OutputDir.Text)) return Fail("出力先を入力してください");

        settings.DefaultMode = ModeManual.IsChecked == true ? CaptureTrigger.Manual
            : ModeWindow.IsChecked == true ? CaptureTrigger.WindowChange : CaptureTrigger.Click;
        settings.OutputDir = OutputDir.Text.Trim();
        settings.DrawWaitMs = drawWait;
        settings.HotkeyCapture = HkCapture.Text.Trim();
        settings.HotkeyPause = HkPause.Text.Trim();
        settings.HotkeyStop = HkStop.Text.Trim();
        settings.GroupMode = (GroupMode)Group.SelectedIndex;
        settings.CaptureCursor = CaptureCursor.IsChecked == true;
        settings.Save(Settings.DefaultPath);
        return true;
    }

    bool Fail(string message)
    {
        if (IsVisible) MessageBox.Show(this, message, "step_recorder", MessageBoxButton.OK, MessageBoxImage.Warning);
        else MessageBox.Show(message, "step_recorder", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    void RefreshProcesses()
    {
        var self = Environment.ProcessId;
        Processes.ItemsSource = Process.GetProcesses()
            .Where(p => p.Id != self && p.MainWindowHandle != 0 && p.MainWindowTitle != "")
            .OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(p => new ProcessItem(p, $"{p.ProcessName} — {p.MainWindowTitle}"))
            .ToList();
    }

    /// <summary>記録開始を待たず、切り替えたらすぐ保存する(次回の起動から効く)。</summary>
    void AutoUpdate_Click(object sender, RoutedEventArgs e)
    {
        settings.AutoUpdate = AutoUpdate.IsChecked == true;
        settings.Save(Settings.DefaultPath);
    }

    // ---- ショートカットキー欄(フォーカス中はキー入力待ち。確定した値は Tag に持つ) ----

    void Hotkey_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var box = (TextBox)sender;
        box.Tag = box.Text;
        box.Text = HotkeyInput.Prompt;
    }

    void Hotkey_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var box = (TextBox)sender;
        if (!Hotkey.TryParse(box.Text, out _)) box.Text = (string)box.Tag; // 確定せずに離れたら元に戻す
    }

    void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        var mods = Keyboard.Modifiers;
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        if (mods == ModifierKeys.None && key == Key.Tab) return; // Tab だけならフォーカス移動
        e.Handled = true;
        if (mods == ModifierKeys.None && key == Key.Escape)
        {
            Group.Focus(); // 取り消し(フォーカスが外れると元の値に戻る)
            return;
        }
        if (HotkeyInput.FromKey(mods, key) is { } hotkey)
        {
            box.Text = hotkey;
            // 確定。隣のキー欄へ移るとそこが入力待ちになってしまうので、キー欄以外へフォーカスを移す
            Group.Focus();
        }
        else box.Text = HotkeyInput.Pending(mods);
    }

    /// <summary>修飾キーを離したら入力待ちの表示を戻す。</summary>
    void Hotkey_PreviewKeyUp(object sender, KeyEventArgs e) => ((TextBox)sender).Text = HotkeyInput.Pending(Keyboard.Modifiers);

    void Refresh_Click(object sender, RoutedEventArgs e) => RefreshProcesses();

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { InitialDirectory = Directory.Exists(OutputDir.Text) ? OutputDir.Text : null };
        if (dlg.ShowDialog(this) == true) OutputDir.Text = dlg.FolderName;
    }

    void Start_Click(object sender, RoutedEventArgs e)
    {
        if (Processes.SelectedItem is not ProcessItem item) { Fail("対象アプリを選んでください"); return; }
        if (item.Process.HasExited) { Fail("対象アプリは終了しています"); RefreshProcesses(); return; }
        if (!ReadSettings()) return;

        var dir = Path.Combine(settings.OutputDir, Settings.SessionFolderName(DateTimeOffset.Now, item.Process.ProcessName));
        Directory.CreateDirectory(dir);
        var bar = new RecordBar(new RecorderEngine(item.Process, dir, settings), settings, dir);
        bar.Closed += (_, _) =>
        {
            if (bar.Result is { Steps.Count: > 0 } session) OpenEditor(session, dir);
            else
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
                Show();
            }
        };
        Hide();
        bar.Show();
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadSettings()) return;
        var dlg = new OpenFileDialog { Filter = $"記録 ({SessionStore.FileName})|{SessionStore.FileName}", InitialDirectory = settings.OutputDir };
        if (dlg.ShowDialog(this) == true) OpenSession(Path.GetDirectoryName(dlg.FileName)!);
    }

    internal void OpenSession(string dir)
    {
        Session session;
        try { session = SessionStore.Load(dir); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            Fail($"開けませんでした: {ex.Message}");
            Show(); // 引数で開こうとして失敗したときも開始画面は出す
            return;
        }
        Hide();
        OpenEditor(session, dir);
    }

    void OpenEditor(Session session, string dir)
    {
        var editor = new EditorWindow(session, dir, settings.GroupMode);
        editor.Closed += (_, _) => Show();
        editor.Show();
    }
}
