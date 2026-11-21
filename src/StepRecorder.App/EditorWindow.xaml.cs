using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using StepRecorder.Core;
using Geometry = StepRecorder.Core.Geometry;

namespace StepRecorder.App;

public partial class EditorWindow : Window
{
    /// <summary>一覧の1行。</summary>
    sealed record StepView(Step Step, BitmapSource? Thumb, string Header, string Summary);

    enum Drag { None, New, Move, Resize }

    readonly Session session;
    readonly string dir;
    readonly Dictionary<string, BitmapSource?> thumbs = [];
    readonly List<string> maybeUnused = []; // 消したステップや足したフレームのファイル。保存したとき、どのステップも使っていなければ消す
    readonly SessionHistory history = new();
    bool dirty, loading;
    bool textCheckpointed; // 文字の編集は、欄で編集を始めてから終えるまでを1回分として控える

    // 画像上のドラッグ
    Drag drag;
    Point dragStart;
    PixelRect? dragOrigin, preview;
    int selectedHighlight = -1;

    // ズーム。fit のときは表示欄の大きさに合わせて自動で変える
    const double MinZoom = 0.1, MaxZoom = 8, ZoomStep = 1.25;
    double zoom = 1;
    bool fit = true;

    Step? Current => (Steps.SelectedItem as StepView)?.Step;

    public EditorWindow(Session session, string dir, GroupMode mode)
    {
        InitializeComponent();
        this.session = session;
        this.dir = dir;
        Title = $"step_recorder 編集 — {dir}";
        loading = true;
        Group.SelectedIndex = (int)mode;
        loading = false;
        ClipFormatBox.SelectedIndex = (int)Settings.Load(Settings.DefaultPath).ClipFormat;
        if (session.VideoFile != null) OpenFullVideo();
        RefreshList(0);
    }

    // ---- 一覧 ----

    void RefreshList(int select)
    {
        Steps.ItemsSource = session.Steps.Select((s, i) => new StepView(s, Thumb(s), $"{i + 1}. {s.ScreenName}{(s.VideoFile != null ? "  ▶ 動画" : "")}", s.Description)).ToList();
        if (session.Steps.Count > 0) Steps.SelectedIndex = Math.Clamp(select, 0, session.Steps.Count - 1);
        else ShowStep(null);
    }

    BitmapSource? Thumb(Step s)
    {
        if (!thumbs.TryGetValue(s.ImageFile, out var t)) thumbs[s.ImageFile] = t = Load(s, 220);
        return t;
    }

    /// <summary>画像を読む。ファイルをロックしないよう OnLoad で読み切る。</summary>
    BitmapSource? Load(Step s, int decodeWidth = 0)
    {
        var path = System.IO.Path.Combine(dir, s.ImageFile);
        if (!File.Exists(path)) return null;
        var b = new BitmapImage();
        b.BeginInit();
        b.UriSource = new Uri(path);
        b.CacheOption = BitmapCacheOption.OnLoad;
        b.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        if (decodeWidth > 0) b.DecodePixelWidth = decodeWidth;
        b.EndInit();
        b.Freeze();
        return b;
    }

    void Steps_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowStep(Current);

    void ShowStep(Step? s)
    {
        loading = true;
        ScreenName.Text = s?.ScreenName ?? "";
        Description.Text = s?.Description ?? "";
        ScreenName.IsEnabled = Description.IsEnabled = s != null;
        loading = false;
        selectedHighlight = -1;
        StopClip();
        PlayButton.Visibility = s?.VideoFile != null ? Visibility.Visible : Visibility.Collapsed;
        ToolAdd.IsEnabled = s?.VideoFile == null; // 動画には強調枠を付けない
        if (s?.VideoFile != null && ToolAdd.IsChecked == true) ToolSelect.IsChecked = true;
        if (s?.VideoTime is { } t && session.VideoFile != null) TimeSlider.Value = t.TotalSeconds; // タイムラインもその時刻へ
        Picture.Source = s == null ? null : Load(s);
        if (s != null)
        {
            // 画像は撮影時のピクセル寸法で置き、ズームは拡大縮小の変換だけで行う。座標はそのまま画像内のピクセル
            Stage.Width = Picture.Width = Overlay.Width = s.ImageWidth;
            Stage.Height = Picture.Height = Overlay.Height = s.ImageHeight;
        }
        if (fit) FitZoom();
        else Redraw();
    }

    void Move(int delta)
    {
        var i = Steps.SelectedIndex;
        var j = i + delta;
        if (i < 0 || j < 0 || j >= session.Steps.Count) return;
        Checkpoint();
        (session.Steps[i], session.Steps[j]) = (session.Steps[j], session.Steps[i]);
        RefreshList(j);
    }

    void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    void Down_Click(object sender, RoutedEventArgs e) => Move(1);

    void DeleteStep_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } s) return;
        var i = Steps.SelectedIndex;
        Checkpoint();
        session.Steps.Remove(s);
        maybeUnused.Add(s.ImageFile); // ファイルは保存したときに消す(元に戻したものは消さない)
        if (s.VideoFile != null) maybeUnused.Add(s.VideoFile);
        RefreshList(i);
    }

    // ---- 文字の編集 ----

    void ScreenName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (loading || Current is not { } s) return;
        TextCheckpoint();
        s.ScreenName = ScreenName.Text;
    }

    void Description_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (loading || Current is not { } s) return;
        TextCheckpoint();
        s.Description = Description.Text.ReplaceLineEndings("\n");
    }

    void TextCheckpoint()
    {
        if (textCheckpointed) return;
        Checkpoint();
        textCheckpointed = true;
    }

    void Field_LostFocus(object sender, RoutedEventArgs e)
    {
        textCheckpointed = false;
        RefreshList(Steps.SelectedIndex);
    }

    // ---- 強調枠とトリミング ----

    /// <summary>画面上の数ピクセルを画像のピクセルに換算する(枠線の太さやつまみの大きさを、ズームに関係なく一定に見せるため)。</summary>
    double ScreenToImage(double px) => px / zoom;

    // ---- ズーム ----

    /// <summary>ズームを変える。anchor(表示欄内の位置)を渡すと、そこに映っている画像上の点が動かないようにスクロールする。</summary>
    void SetZoom(double z, Point? anchor = null)
    {
        var at = anchor ?? new Point(Scroller.ViewportWidth / 2, Scroller.ViewportHeight / 2);
        var imagePoint = Scroller.TranslatePoint(at, Stage);
        zoom = Math.Clamp(z, MinZoom, MaxZoom);
        Zoom.ScaleX = Zoom.ScaleY = zoom;
        ZoomLabel.Text = $"{zoom * 100:0}%";
        Scroller.UpdateLayout();
        var moved = Stage.TranslatePoint(imagePoint, Scroller);
        Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset + moved.X - at.X);
        Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + moved.Y - at.Y);
        Redraw();
    }

    /// <summary>画像全体が表示欄に収まる大きさにする(小さい画像は拡大しない)。</summary>
    void FitZoom()
    {
        fit = true;
        var margin = Stage.Margin.Left + Stage.Margin.Right + 2;
        if (Current is not { } s || Scroller.ActualWidth <= margin || Scroller.ActualHeight <= margin) { Redraw(); return; }
        SetZoom(Math.Min(1, Math.Min((Scroller.ActualWidth - margin) / s.ImageWidth, (Scroller.ActualHeight - margin) / s.ImageHeight)));
    }

    void ZoomBy(double factor, Point? anchor = null)
    {
        fit = false;
        SetZoom(zoom * factor, anchor);
    }

    void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomBy(ZoomStep);
    void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomBy(1 / ZoomStep);
    void ZoomFit_Click(object sender, RoutedEventArgs e) => FitZoom();

    void ZoomActual_Click(object sender, RoutedEventArgs e)
    {
        fit = false;
        SetZoom(1);
    }

    /// <summary>Ctrl+ホイールでマウスの位置を中心に拡大縮小する(Ctrl なしは通常のスクロール)。</summary>
    void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        ZoomBy(e.Delta > 0 ? ZoomStep : 1 / ZoomStep, e.GetPosition(Scroller));
    }

    // ホイールボタン(中ボタン)のドラッグで画像をつかんで動かす
    Point? panFrom;
    double panFromX, panFromY;

    void Scroller_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Middle) return;
        panFrom = e.GetPosition(Scroller);
        (panFromX, panFromY) = (Scroller.HorizontalOffset, Scroller.VerticalOffset);
        Scroller.CaptureMouse();
        Scroller.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    void Scroller_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (panFrom is not { } from) return;
        var p = e.GetPosition(Scroller);
        Scroller.ScrollToHorizontalOffset(panFromX - (p.X - from.X));
        Scroller.ScrollToVerticalOffset(panFromY - (p.Y - from.Y));
    }

    void Scroller_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Middle || panFrom == null) return;
        panFrom = null;
        Scroller.ReleaseMouseCapture();
        Scroller.Cursor = null;
        e.Handled = true;
    }

    void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (fit) FitZoom();
    }

    void Redraw()
    {
        Overlay.Children.Clear();
        if (Current is not { } s) return;
        var stroke = ScreenToImage(2.5); // ズームに関係なく画面上で同じ太さに見せる

        if (s.Crop is { } c) // トリミングの外側を暗くする
        {
            var shade = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0));
            AddRect(new(0, 0, s.ImageWidth, c.Y), shade, null, 0);
            AddRect(new(0, c.Y + c.Height, s.ImageWidth, s.ImageHeight - c.Y - c.Height), shade, null, 0);
            AddRect(new(0, c.Y, c.X, c.Height), shade, null, 0);
            AddRect(new(c.X + c.Width, c.Y, s.ImageWidth - c.X - c.Width, c.Height), shade, null, 0);
        }
        for (var i = 0; i < s.Highlights.Count; i++)
        {
            var sel = i == selectedHighlight;
            var r = sel && preview != null && drag != Drag.New ? preview : s.Highlights[i];
            AddRect(r, null, sel ? Brushes.Orange : Brushes.Red, stroke);
            if (sel) // 右下のつまみ
            {
                var k = ScreenToImage(10);
                AddRect(new((int)(r.X + r.Width - k / 2), (int)(r.Y + r.Height - k / 2), (int)k, (int)k), Brushes.Orange, null, 0);
            }
        }
        if (drag == Drag.New && preview != null)
            AddRect(preview, null, ToolCrop.IsChecked == true ? Brushes.DodgerBlue : Brushes.Red, stroke, dashed: true);
    }

    void AddRect(PixelRect r, Brush? fill, Brush? stroke, double thickness, bool dashed = false)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        var rect = new Rectangle { Width = r.Width, Height = r.Height, Fill = fill, Stroke = stroke, StrokeThickness = thickness, IsHitTestVisible = false };
        if (dashed) rect.StrokeDashArray = [4, 2];
        Canvas.SetLeft(rect, r.X);
        Canvas.SetTop(rect, r.Y);
        Overlay.Children.Add(rect);
    }

    void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Current is not { } s) return;
        Overlay.Focus();
        dragStart = e.GetPosition(Overlay);
        preview = null;
        if (ToolAdd.IsChecked == true || ToolCrop.IsChecked == true)
            drag = Drag.New;
        else
        {
            var tol = ScreenToImage(8);
            selectedHighlight = -1;
            drag = Drag.None;
            for (var i = s.Highlights.Count - 1; i >= 0; i--) // 手前(後から足した)ものを優先
            {
                var h = s.Highlights[i];
                if (Geometry.NearBottomRight(h, dragStart.X, dragStart.Y, tol)) { selectedHighlight = i; drag = Drag.Resize; break; }
                if (Geometry.Contains(h, dragStart.X, dragStart.Y)) { selectedHighlight = i; drag = Drag.Move; break; }
            }
            dragOrigin = selectedHighlight >= 0 ? s.Highlights[selectedHighlight] : null;
        }
        if (drag != Drag.None) Overlay.CaptureMouse();
        Redraw();
    }

    void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (drag == Drag.None || Current is not { } s) return;
        var p = e.GetPosition(Overlay);
        preview = drag switch
        {
            Drag.New => Geometry.FromPoints(dragStart.X, dragStart.Y, p.X, p.Y, s.ImageWidth, s.ImageHeight),
            Drag.Move => Geometry.Move(dragOrigin!, p.X - dragStart.X, p.Y - dragStart.Y, s.ImageWidth, s.ImageHeight),
            Drag.Resize => Geometry.ResizeBottomRight(dragOrigin!, p.X, p.Y, s.ImageWidth, s.ImageHeight),
            _ => null,
        };
        Redraw();
    }

    void Overlay_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (drag == Drag.None || Current is not { } s) return;
        Overlay.ReleaseMouseCapture();
        if (preview != null)
        {
            if (drag == Drag.New && Geometry.IsUsable(preview))
            {
                Checkpoint();
                if (ToolCrop.IsChecked == true) s.Crop = preview;
                else { s.Highlights.Add(preview); selectedHighlight = s.Highlights.Count - 1; }
            }
            else if (drag is Drag.Move or Drag.Resize && preview != dragOrigin)
            {
                Checkpoint();
                s.Highlights[selectedHighlight] = preview;
            }
        }
        drag = Drag.None;
        preview = null;
        Redraw();
    }

    void DeleteHighlight_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } s || selectedHighlight < 0 || selectedHighlight >= s.Highlights.Count) return;
        Checkpoint();
        s.Highlights.RemoveAt(selectedHighlight);
        selectedHighlight = -1;
        Redraw();
    }

    void ClearCrop_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { Crop: not null } s) return;
        Checkpoint();
        s.Crop = null;
        Redraw();
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { Save(); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract or Key.D0 or Key.NumPad0)
        {
            if (e.Key is Key.D0 or Key.NumPad0) ZoomActual_Click(this, new());
            else ZoomBy(e.Key is Key.OemPlus or Key.Add ? ZoomStep : 1 / ZoomStep);
            e.Handled = true;
            return;
        }
        if (Keyboard.FocusedElement is TextBox) return; // 入力中の Ctrl+Z などは TextBox 自身に任せる
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { Undo(); e.Handled = true; return; }
        if (e.Key == Key.Delete) { DeleteHighlight_Click(this, new()); e.Handled = true; }
        if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.Up or Key.Down)
        {
            Move(e.SystemKey == Key.Up ? -1 : 1);
            e.Handled = true;
        }
    }

    // ---- 動画 ----

    readonly System.Windows.Threading.DispatcherTimer playTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    bool clipPlaying, fullPlaying, sliderFromPlayer;
    TimeSpan? rangeStart, rangeEnd;

    string FullPath(string relative) => System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, relative));

    /// <summary>動画のステップを、区間だけ再生する(ポスターの上に重ねる)。もう一度押すと止める。</summary>
    void Play_Click(object sender, RoutedEventArgs e)
    {
        if (clipPlaying) { StopClip(); return; }
        if (Current is not { VideoFile: { } video } s) return;
        Player.Source = new Uri(FullPath(video));
        Player.Position = s.VideoStart ?? TimeSpan.Zero;
        Player.Visibility = Visibility.Visible;
        Player.Play();
        clipPlaying = true;
        PlayButton.Content = "■ 停止";
        StartPlayTimer();
    }

    void StopClip()
    {
        if (!clipPlaying) return;
        clipPlaying = false;
        Player.Stop();
        Player.Source = null;
        Player.Visibility = Visibility.Collapsed;
        PlayButton.Content = "▶ 再生";
    }

    void Player_MediaEnded(object sender, RoutedEventArgs e) => StopClip();

    void StartPlayTimer()
    {
        if (playTimer.IsEnabled) return;
        playTimer.Tick += (_, _) => OnPlayTick();
        playTimer.Start();
    }

    void OnPlayTick()
    {
        if (clipPlaying && Current?.VideoEnd is { } end && Player.Position >= end) StopClip();
        if (fullPlaying)
        {
            sliderFromPlayer = true;
            TimeSlider.Value = FullPlayer.Position.TotalSeconds;
            sliderFromPlayer = false;
        }
    }

    void OpenFullVideo()
    {
        TimelinePanel.Visibility = Visibility.Visible;
        FullPlayer.Source = new Uri(FullPath(session.VideoFile!));
        FullPlayer.Pause(); // 開くだけ(MediaOpened で長さが分かる)
        UpdateTicks();
    }

    void FullPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (FullPlayer.NaturalDuration.HasTimeSpan) TimeSlider.Maximum = FullPlayer.NaturalDuration.TimeSpan.TotalSeconds;
        FullPlayer.Position = TimeSpan.FromSeconds(TimeSlider.Value);
    }

    void FullPlayer_MediaEnded(object sender, RoutedEventArgs e) => FullPlay_Click(this, new());

    /// <summary>タイムラインの目盛り(ステップを撮った時刻)を付け直す。</summary>
    void UpdateTicks() =>
        TimeSlider.Ticks = new DoubleCollection(session.Steps.Where(s => s.VideoTime != null).Select(s => s.VideoTime!.Value.TotalSeconds));

    TimeSpan SliderTime => TimeSpan.FromSeconds(TimeSlider.Value);

    static string Clock(TimeSpan t) => t.ToString(@"m\:ss\.f");

    void TimeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        TimeLabel.Text = Clock(SliderTime);
        if (!sliderFromPlayer && FullPlayer.Source != null) FullPlayer.Position = SliderTime;
    }

    void FullPlay_Click(object sender, RoutedEventArgs e)
    {
        fullPlaying = !fullPlaying;
        if (fullPlaying) { FullPlayer.Play(); StartPlayTimer(); } else FullPlayer.Pause();
        FullPlayButton.Content = fullPlaying ? "‖" : "▶";
    }

    void AddFrame_Click(object sender, RoutedEventArgs e)
    {
        var time = SliderTime;
        InsertFromVideo(CaptureTrigger.Manual, VideoClips.Frame(FullPath(session.VideoFile!), time), time, _ => { });
    }

    void RangeStart_Click(object sender, RoutedEventArgs e) { rangeStart = SliderTime; ShowRange(); }
    void RangeEnd_Click(object sender, RoutedEventArgs e) { rangeEnd = SliderTime; ShowRange(); }

    void ShowRange() =>
        RangeLabel.Text = $"区間 {(rangeStart is { } a ? Clock(a) : "?")} 〜 {(rangeEnd is { } b ? Clock(b) : "?")}";

    void AddRange_Click(object sender, RoutedEventArgs e)
    {
        if (rangeStart is not { } start || rangeEnd is not { } end || end <= start)
        {
            MessageBox.Show(this, "「区間の開始」と「区間の終了」で、開始より後に終了を指定してください。", "step_recorder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        InsertFromVideo(CaptureTrigger.Video, VideoClips.Frame(FullPath(session.VideoFile!), start), start,
            st => (st.VideoFile, st.VideoStart, st.VideoEnd) = (session.VideoFile, start, end));
        (rangeStart, rangeEnd) = (null, null);
        RangeLabel.Text = "";
    }

    /// <summary>全編の動画の time から作ったステップを、時刻の順の位置に入れる。画面名などは直前のステップに合わせる。</summary>
    void InsertFromVideo(CaptureTrigger trigger, BitmapSource image, TimeSpan time, Action<Step> setup)
    {
        Checkpoint();
        var rec = new Recording(session);
        // 消したステップの画像(保存までは残っていて、元に戻せる)と名前がぶつからないよう時刻を付ける
        var file = $"images/{rec.NextId:D4}-{DateTime.Now:HHmmssfff}.png";
        WindowCapture.SavePng(image, System.IO.Path.Combine(dir, file));
        maybeUnused.Add(file);
        var index = Recording.IndexByVideoTime(session.Steps, time);
        var near = index > 0 ? session.Steps[index - 1] : session.Steps.FirstOrDefault();
        var st = rec.AddStep(trigger, near?.Window ?? new("", "", session.ProcessName), file, image.PixelWidth, image.PixelHeight, DateTimeOffset.Now);
        st.ScreenName = near?.ScreenName ?? "";
        st.VideoTime = time;
        setup(st);
        session.Steps.Remove(st);
        session.Steps.Insert(index, st);
        UpdateTicks();
        RefreshList(index);
    }

    void ExportForDistribution_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "配布用に書き出すフォルダ", InitialDirectory = System.IO.Path.GetDirectoryName(dir) };
        if (dialog.ShowDialog(this) != true) return;
        var format = (ClipFormat)ClipFormatBox.SelectedIndex;
        var settings = Settings.Load(Settings.DefaultPath);
        settings.ClipFormat = format;
        settings.Save(Settings.DefaultPath);
        // ponytail: 動画はフレームを1枚ずつ取り出して作り直すので、長いと待たされる。気になるなら進み具合を出す
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            Export.ExportForDistribution(session, dir, dialog.FolderName, (GroupMode)Group.SelectedIndex, format,
                (st, clip, poster) => VideoClips.ExportClip(dir, st, format, clip, poster));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, $"書き出せませんでした: {ex.Message}", "step_recorder", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally { Mouse.OverrideCursor = null; }
        Process.Start(new ProcessStartInfo(System.IO.Path.Combine(dialog.FolderName, Export.HtmlFile)) { UseShellExecute = true });
    }

    // ---- 保存 ----

    void Group_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!loading) MarkDirty();
    }

    void MarkDirty() => dirty = true;

    // ---- 元に戻す ----

    /// <summary>変更する直前に呼ぶ。今の状態を履歴に控える。</summary>
    void Checkpoint()
    {
        history.Push(session);
        dirty = true;
        UndoButton.IsEnabled = true;
    }

    void Undo()
    {
        if (history.Undo() is not { } previous) return;
        var i = Steps.SelectedIndex;
        session.Steps = previous.Steps;
        dirty = true;
        textCheckpointed = false;
        UndoButton.IsEnabled = history.CanUndo;
        RefreshList(i);
    }

    void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    bool Save()
    {
        try
        {
            Export.SaveAll(session, dir, (GroupMode)Group.SelectedIndex);
            foreach (var f in maybeUnused.Distinct().Where(f => f != session.VideoFile && session.Steps.All(s => s.ImageFile != f && s.VideoFile != f)))
                File.Delete(System.IO.Path.Combine(dir, f));
            maybeUnused.Clear();
            dirty = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"保存できませんでした: {ex.Message}", "step_recorder", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    void Save_Click(object sender, RoutedEventArgs e) => Save();

    void OpenHtml_Click(object sender, RoutedEventArgs e)
    {
        if ((dirty || !File.Exists(System.IO.Path.Combine(dir, Export.HtmlFile))) && !Save()) return;
        Process.Start(new ProcessStartInfo(System.IO.Path.Combine(dir, Export.HtmlFile)) { UseShellExecute = true });
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (dirty)
        {
            var r = MessageBox.Show(this, "変更を保存しますか?", "step_recorder", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel || (r == MessageBoxResult.Yes && !Save())) e.Cancel = true;
        }
        base.OnClosing(e);
    }
}
