using System.Diagnostics;
using System.IO;
using System.Windows;
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
        Version.Text = $"バージョン {Updater.CurrentVersion}";
        ShowSettings();
        RefreshProcesses();
        Loaded += async (_, _) => await Updater.CheckAsync(this);
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
        Delay.Text = settings.WindowChangeDelayMs.ToString();
        HkCapture.Text = settings.HotkeyCapture;
        HkPause.Text = settings.HotkeyPause;
        HkStop.Text = settings.HotkeyStop;
        Group.SelectedIndex = (int)settings.GroupMode;
    }

    /// <summary>画面の入力を設定に反映して保存する。不正な値があればメッセージを出して false。</summary>
    bool ReadSettings()
    {
        if (!int.TryParse(Delay.Text, out var delay) || delay < 0) return Fail("待ち時間は 0 以上の数値で入力してください");
        foreach (var (box, label) in new[] { (HkCapture, "手動撮影"), (HkPause, "一時停止"), (HkStop, "停止") })
            if (!Hotkey.TryParse(box.Text, out _)) return Fail($"{label}キーは Ctrl+Shift+F9 の形式で入力してください");
        if (string.IsNullOrWhiteSpace(OutputDir.Text)) return Fail("出力先を入力してください");

        settings.DefaultMode = ModeManual.IsChecked == true ? CaptureTrigger.Manual
            : ModeWindow.IsChecked == true ? CaptureTrigger.WindowChange : CaptureTrigger.Click;
        settings.OutputDir = OutputDir.Text.Trim();
        settings.WindowChangeDelayMs = delay;
        settings.HotkeyCapture = HkCapture.Text.Trim();
        settings.HotkeyPause = HkPause.Text.Trim();
        settings.HotkeyStop = HkStop.Text.Trim();
        settings.GroupMode = (GroupMode)Group.SelectedIndex;
        settings.Save(Settings.DefaultPath);
        return true;
    }

    bool Fail(string message)
    {
        MessageBox.Show(this, message, "step_recorder", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        if (dlg.ShowDialog(this) != true) return;
        var dir = Path.GetDirectoryName(dlg.FileName)!;
        Session session;
        try { session = SessionStore.Load(dir); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            Fail($"開けませんでした: {ex.Message}");
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
