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
    sealed record TargetItem(CaptureTarget Target, string Label)
    {
        public override string ToString() => Label; // 一覧の項目の読み上げ名になる
    }

    MonitorBadge? badge;
    bool refreshing;
    Settings settings = Settings.Load(Settings.DefaultPath);

    public MainWindow()
    {
        InitializeComponent();
        if (settings.MainBounds != null) WindowStartupLocation = WindowStartupLocation.Manual; // 前回の位置に置く(OnSourceInitialized)
        Version.Text = $"バージョン {AppVersion.Current}";
        ShowSettings();
        RefreshTargets();
        IsVisibleChanged += (_, _) => { if (IsVisible) RefreshTargets(); }; // 記録や編集から戻ったときに古くならないように
        Loaded += async (_, _) =>
        {
            if (settings.AutoUpdate) await Updater.CheckAsync(this);
        };
    }

    void Version_Click(object sender, MouseButtonEventArgs e) =>
        Process.Start(new ProcessStartInfo(Updater.Repo) { UseShellExecute = true });

    void ShowSettings()
    {
        (settings.DefaultMode switch { RecordMode.Manual => ModeManual, RecordMode.Video => ModeRecord, _ => ModeClick }).IsChecked = true;
        OutputDir.Text = settings.OutputDir;
        HkCapture.Text = settings.HotkeyCapture;
        HkPause.Text = settings.HotkeyPause;
        HkStop.Text = settings.HotkeyStop;
        HkClip.Text = settings.HotkeyClip;
        Group.SelectedIndex = (int)settings.GroupMode;
        CaptureCursor.IsChecked = settings.CaptureCursor;
        Fps.ItemsSource = FrameRate.Choices.Select(FrameRate.Label).ToList();
        Fps.SelectedIndex = Array.IndexOf(FrameRate.Choices, FrameRate.Valid(settings.Fps));
        AutoUpdate.IsChecked = settings.AutoUpdate;
    }

    /// <summary>画面の入力を設定に反映して保存する。不正な値があればメッセージを出して false。</summary>
    bool ReadSettings()
    {
        if (HotkeyError() is { } message) return Fail(message);
        if (string.IsNullOrWhiteSpace(OutputDir.Text)) return Fail("出力先を入力してください");
        WriteSettings(hotkeys: true);
        return true;
    }

    /// <summary>ショートカットキー欄がおかしければ、その知らせ。</summary>
    string? HotkeyError()
    {
        foreach (var (box, label) in new[] { (HkCapture, "手動撮影"), (HkPause, "一時停止"), (HkStop, "停止"), (HkClip, "録画") })
            if (!Hotkey.TryParse(box.Text, out _)) return $"{label}キーを設定してください(欄をクリックしてキーを押す)";
        if (new[] { HkCapture.Text, HkPause.Text, HkStop.Text, HkClip.Text }.Distinct().Count() < 4) return "手動撮影・一時停止・停止・録画には別々のキーを設定してください";
        return null;
    }

    /// <summary>画面の入力を設定に保存する。hotkeys が false ならショートカットキーは前のまま。出力先が空なら前のまま。</summary>
    void WriteSettings(bool hotkeys) => settings = Settings.Update(Settings.DefaultPath, s =>
    {
        s.DefaultMode = ModeManual.IsChecked == true ? RecordMode.Manual
            : ModeRecord.IsChecked == true ? RecordMode.Video : RecordMode.Click;
        if (OutputDir.Text.Trim() is { Length: > 0 } dir) s.OutputDir = dir;
        if (hotkeys)
        {
            s.HotkeyCapture = HkCapture.Text.Trim();
            s.HotkeyPause = HkPause.Text.Trim();
            s.HotkeyStop = HkStop.Text.Trim();
            s.HotkeyClip = HkClip.Text.Trim();
        }
        s.GroupMode = (GroupMode)Group.SelectedIndex;
        s.CaptureCursor = CaptureCursor.IsChecked == true;
        s.Fps = FrameRate.Choices[Math.Max(0, Fps.SelectedIndex)];
    });

    bool Fail(string message)
    {
        if (IsVisible) MessageBox.Show(this, message, "step_recorder", MessageBoxButton.OK, MessageBoxImage.Warning);
        else MessageBox.Show(message, "step_recorder", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    /// <summary>対象の一覧。モニターを先に、そのあとにアプリを並べる。選んでいたものは選んだままにする。</summary>
    void RefreshTargets()
    {
        var monitors = Monitors.All().Select(m => new TargetItem(new MonitorTarget(m), m.Label));
        var self = Environment.ProcessId;
        var apps = Process.GetProcesses()
            .Where(p => p.Id != self && p.MainWindowTitle != "" && AppProcesses.IsAppWindow(p.MainWindowHandle))
            .OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(p => new TargetItem(new AppTarget(p), $"{p.ProcessName} — {p.MainWindowTitle}"));
        var items = monitors.Concat(apps).ToList();
        var selected = Targets.SelectedItem as TargetItem;
        refreshing = true;
        Targets.ItemsSource = items;
        Targets.SelectedItem = selected == null ? null
            : items.FirstOrDefault(i => i.Label == selected.Label && i.Target.IsSameTarget(selected.Target))
                ?? items.FirstOrDefault(i => i.Target.IsSameTarget(selected.Target)); // タブが切り替わって題が変わっても同じアプリ
        refreshing = false;
    }

    void Targets_DropDownOpened(object? sender, EventArgs e) => RefreshTargets();

    /// <summary>モニターを選んだら、どのモニターかを枠で示す。</summary>
    void Targets_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (refreshing) return; // 一覧の作り直しでは枠を出し直さない
        CloseBadge();
        if (Targets.SelectedItem is not TargetItem { Target: MonitorTarget target }) return;
        badge = new MonitorBadge(target.Monitor);
        badge.Closed += (s, _) => { if (badge == s) badge = null; };
        badge.Show();
    }

    void CloseBadge() => badge?.Close();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        SavedPlacement.Restore(this, settings.MainBounds, keepSize: true);
    }

    /// <summary>位置を覚える。記録フォルダを渡して起動し、開始画面を一度も出していなければ覚えない。</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || !IsLoaded || SavedPlacement.Capture(this) is not { } bounds) return;
        try { Settings.Update(Settings.DefaultPath, s => s.MainBounds = bounds); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // 覚えられなくても閉じるのは止めない
    }

    protected override void OnClosed(EventArgs e)
    {
        CloseBadge();
        // 記録を始めなくても残す。閉じるときは知らせを出さず、使えないショートカットキーと空の出力先は前のままにする
        WriteSettings(hotkeys: HotkeyError() == null);
        base.OnClosed(e);
    }

    /// <summary>記録開始を待たず、切り替えたらすぐ保存する(次回の起動から効く)。</summary>
    void AutoUpdate_Click(object sender, RoutedEventArgs e)
    {
        settings = Settings.Update(Settings.DefaultPath, s => s.AutoUpdate = AutoUpdate.IsChecked == true);
    }

    async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdate.IsEnabled = false; // 確認中の二度押しを防ぐ
        await Updater.CheckAsync(this, manual: true);
        CheckUpdate.IsEnabled = true;
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

    void Refresh_Click(object sender, RoutedEventArgs e) => RefreshTargets();

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { InitialDirectory = Directory.Exists(OutputDir.Text) ? OutputDir.Text : null };
        if (dlg.ShowDialog(this) == true) OutputDir.Text = dlg.FolderName;
    }

    void Start_Click(object sender, RoutedEventArgs e)
    {
        if (Targets.SelectedItem is not TargetItem { Target: var target }) { Fail("対象(画面かアプリ)を選んでください"); return; }
        if (!target.IsAvailable)
        {
            Fail(target is MonitorTarget ? "その画面は外されたか、解像度や位置が変わっています。選び直してください" : "対象アプリのウィンドウが見つかりません(終了したか、隠れています)");
            RefreshTargets();
            return;
        }
        if (!ReadSettings()) return;
        Session? existing = null;
        if (appendDir != null && (existing = LoadForAppend(appendDir)) == null) return;
        CloseBadge();

        var dir = appendDir ?? Path.Combine(settings.OutputDir, Settings.SessionFolderName(DateTimeOffset.Now, target.Name));
        EndAppend();
        Directory.CreateDirectory(dir);
        var recordVideo = settings.DefaultMode == RecordMode.Video;
        var videosBefore = existing?.Videos.Count ?? 0;
        var log = Path.Combine(dir, "error.log");
        var loggedBefore = File.Exists(log); // 続けて記録では、前の記録のときのものが残っていることがある
        var bar = new RecordBar(new RecorderEngine(target, dir, settings, existing), settings, dir);
        bar.Closed += (_, _) =>
        {
            var failed = File.Exists(log) && !loggedBefore;
            var kept = bar.Result is { HasContent: true };
            // 続けて記録では、新しく何も撮れなくても前の記録があるので、フォルダは消さずに編集画面に戻る
            if (kept || existing != null) OpenEditor(bar.Result ?? SessionStore.Load(dir), dir);
            else
            {
                if (!failed) try { Directory.Delete(dir, true); } catch (IOException) { } // 失敗したときは理由(error.log)を残す
                Show();
            }
            bar.Progress?.Finish();
            if (recordVideo && (bar.Result?.Videos.Count ?? 0) <= videosBefore) Fail($"録画できませんでした。理由はこのファイルに書いてあります:\n{log}");
            else if (!kept && failed) Fail($"記録できませんでした。理由はこのファイルに書いてあります:\n{log}");
        };
        Hide();
        bar.Show();
    }

    // ---- 続けて記録(編集画面から戻ってきて、同じ記録の末尾に足す) ----

    /// <summary>続けて記録する記録のフォルダ。null なら新しい記録。</summary>
    string? appendDir;

    void BeginAppend(string targetName, string dir)
    {
        appendDir = dir;
        AppendLabel.Text = $"「{targetName}」の記録に続けて記録します。対象と撮影モードを選んで「記録開始」を押してください。";
        AppendPanel.Visibility = Visibility.Visible;
        OutputDir.IsEnabled = BrowseButton.IsEnabled = false; // 保存先はその記録のフォルダ
    }

    void EndAppend()
    {
        appendDir = null;
        AppendPanel.Visibility = Visibility.Collapsed;
        OutputDir.IsEnabled = BrowseButton.IsEnabled = true;
    }

    /// <summary>続けて記録する記録を読む。読めなければ知らせて、新しい記録に戻す。</summary>
    Session? LoadForAppend(string dir)
    {
        try { return SessionStore.Load(dir); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            EndAppend();
            Fail($"続けて記録する記録を開けませんでした: {ex.Message}");
            return null;
        }
    }

    void CancelAppend_Click(object sender, RoutedEventArgs e)
    {
        var dir = appendDir!;
        EndAppend();
        OpenSession(dir);
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadSettings()) return;
        // 名前にパターンは書かない(拡張子を表示する設定だと、ダイアログが自分で付け足して2つになる)
        var dlg = new OpenFileDialog { Filter = $"記録|{SessionStore.FileName}", InitialDirectory = settings.OutputDir };
        if (dlg.ShowDialog(this) == true) OpenSession(Path.GetDirectoryName(dlg.FileName)!);
    }

    /// <summary>記録を開く。記録を止めたときと同じく、編集画面が出るまで進み具合を出す(画像の多い記録は開くのに時間がかかる)。</summary>
    internal async void OpenSession(string dir)
    {
        EndAppend(); // 別の記録を開いたら、続けて記録はやめる
        var progress = new ProgressWindow("記録を開いています", "記録を読み込んでいます...", null);
        progress.Show();
        Session session;
        try { session = await Task.Run(() => SessionStore.Load(dir)); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            progress.Finish();
            Fail($"開けませんでした: {ex.Message}");
            Show(); // 引数で開こうとして失敗したときも開始画面は出す
            return;
        }
        progress.Report("編集画面を開いています");
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle); // 編集画面を作る間は描けないので、先に文言を描かせる
        Hide();
        OpenEditor(session, dir);
        progress.Finish();
    }

    void OpenEditor(Session session, string dir)
    {
        var editor = new EditorWindow(session, dir, settings.GroupMode);
        editor.Closed += (_, _) =>
        {
            if (editor.ContinueRequested) BeginAppend(session.TargetName, dir);
            Show();
        };
        editor.Show();
        Native.BringToFront(new System.Windows.Interop.WindowInteropHelper(editor).Handle); // 記録を止めたあと、対象アプリの後ろに隠れないように
    }
}
