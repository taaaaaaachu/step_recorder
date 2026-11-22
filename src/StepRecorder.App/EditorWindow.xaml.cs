using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
    sealed record StepView(Step Step, BitmapSource? Thumb, string Header, string Summary, Screen Screen);

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
        Player.Ended += Player_Ended;
        Player.Failed += Player_Failed;
        this.session = session;
        this.dir = dir;
        Title = $"step_recorder 編集 — {dir}";
        loading = true;
        Group.SelectedIndex = (int)mode;
        foreach (var speed in PlaybackSpeed.Choices) SpeedBox.Items.Add(PlaybackSpeed.Label(speed));
        loading = false;
        ClipFormatBox.SelectedIndex = (int)Settings.Load(Settings.DefaultPath).ClipFormat;
        if (session.VideoFile != null) OpenFullVideo();
        RefreshList(0);
    }

    // ---- 一覧 ----

    /// <summary>一覧を作り直し、session.Steps の select 番目を選ぶ。画面一覧モードでは出力と同じく画面ごとに区切る。</summary>
    void RefreshList(int select)
    {
        var items = session.Steps.Select((s, i) =>
            new StepView(s, Thumb(s), $"{i + 1}. {s.ScreenName}{VideoMark(s)}", s.Description, Export.ScreenOf(s))).ToList();
        Steps.ItemsSource = items;
        var view = CollectionViewSource.GetDefaultView(items);
        if ((GroupMode)Group.SelectedIndex == GroupMode.ByScreen) view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(StepView.Screen)));
        if (items.Count > 0) Steps.SelectedItem = items[Math.Clamp(select, 0, items.Count - 1)];
        else ShowStep(null);
    }

    static string VideoMark(Step s) =>
        s.VideoFile == null ? "" : s.Speed == 1 ? "  ▶ 動画" : $"  ▶ {PlaybackSpeed.Label(s.Speed)}"; // 一覧の幅に収まる長さに

    /// <summary>選んでいるステップの、session.Steps での位置(一覧の表示順とは限らない)。</summary>
    int CurrentIndex => Current is { } s ? session.Steps.IndexOf(s) : -1;

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
        SpeedBox.SelectedIndex = Array.IndexOf(PlaybackSpeed.Choices, s?.Speed ?? 1);
        ScreenName.IsEnabled = Description.IsEnabled = s != null;
        loading = false;
        selectedHighlight = -1;
        LeaveVideoView();
        StopClip();
        PlayButton.Visibility = SpeedBox.Visibility = s?.VideoFile != null ? Visibility.Visible : Visibility.Collapsed;
        ToolAdd.IsEnabled = s?.VideoFile == null; // 動画には強調枠を付けない
        if (s?.VideoFile != null && ToolAdd.IsChecked == true) ToolSelect.IsChecked = true;
        if (s?.VideoTime is { } t && session.VideoFile != null) MoveSlider(t); // タイムラインもその時刻へ
        Picture.Source = s == null ? null : Load(s);
        // 画像は撮影時のピクセル寸法で置き、ズームは拡大縮小の変換だけで行う。座標はそのまま画像内のピクセル
        if (s != null) SetStageSize(s.ImageWidth, s.ImageHeight);
        if (fit) FitZoom();
        else Redraw();
    }

    void Move(int delta)
    {
        var i = CurrentIndex;
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
        var i = CurrentIndex;
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
        RefreshList(CurrentIndex);
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
        if (Current == null && !videoView || double.IsNaN(Stage.Width) || Scroller.ActualWidth <= margin || Scroller.ActualHeight <= margin) { Redraw(); return; }
        SetZoom(Math.Min(1, Math.Min((Scroller.ActualWidth - margin) / Stage.Width, (Scroller.ActualHeight - margin) / Stage.Height)));
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
    // メインの表示欄の Player は、動画のステップの再生(clipPlaying)と、録画モードの全編の表示(videoView)の両方に使う。
    // 全編の表示はシークバーを動かすか ▶ を押すと始まり、ステップを選ぶとそのステップの画像に戻る。

    readonly System.Windows.Threading.DispatcherTimer playTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    bool clipPlaying, videoView, fullPlaying, sliderQuiet;
    (int Width, int Height) fullSize;
    TimeSpan? rangeStart, rangeEnd;

    string FullPath(string relative) => System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, relative));

    void SetStageSize(int width, int height)
    {
        Stage.Width = Picture.Width = Overlay.Width = width;
        Stage.Height = Picture.Height = Overlay.Height = height;
    }

    /// <summary>動画のステップを、区間だけ再生する(ポスターの上に重ねる)。もう一度押すと止める。</summary>
    void Play_Click(object sender, RoutedEventArgs e)
    {
        if (clipPlaying) { StopClip(); return; }
        if (Current is not { VideoFile: { } video } s) return;
        if (videoView) ShowStep(s); // 全編を見ていたら、そのステップの表示に戻してから
        Player.Open(FullPath(video), s.VideoStart ?? TimeSpan.Zero, s.VideoEnd, s.Speed);
        Player.Visibility = Visibility.Visible;
        Player.Play();
        clipPlaying = true;
        PlayButton.Content = "■ 停止";
    }

    /// <summary>動画のステップの速さを変える。再生中なら、その速さで区間の最初から再生し直す。</summary>
    void Speed_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || SpeedBox.SelectedIndex < 0 || Current is not { VideoFile: not null } s) return;
        var speed = PlaybackSpeed.Choices[SpeedBox.SelectedIndex];
        if (speed == s.Speed) return;
        var replay = clipPlaying;
        Checkpoint();
        s.VideoSpeed = speed == 1 ? null : speed;
        RefreshList(CurrentIndex); // 一覧の表示を変える(再生は止まる)
        if (replay) Play_Click(this, new());
    }

    void StopClip()
    {
        if (!clipPlaying) return;
        clipPlaying = false;
        Player.Close();
        Player.Visibility = Visibility.Collapsed;
        PlayButton.Content = "▶ 再生";
    }

    void Player_Ended()
    {
        if (!videoView) { StopClip(); return; }
        MoveSlider(Player.Position);
        SetFullPlaying(false);
    }

    void Player_Failed(string message)
    {
        if (videoView) SetFullPlaying(false);
        else StopClip();
        MessageBox.Show(this, $"動画を表示できませんでした: {message}", "step_recorder", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    void StartPlayTimer()
    {
        if (playTimer.IsEnabled) return;
        playTimer.Tick += (_, _) => OnPlayTick();
        playTimer.Start();
    }

    void OnPlayTick()
    {
        if (fullPlaying) MoveSlider(Player.Position);
    }

    void OpenFullVideo()
    {
        TimelinePanel.Visibility = Visibility.Visible;
        var (duration, width, height) = VideoClips.Info(FullPath(session.VideoFile!));
        TimeSlider.Maximum = duration.TotalSeconds;
        fullSize = (width, height);
        UpdateTicks();
    }

    /// <summary>表示欄に全編の動画を出す(ステップの画像と強調枠の編集は隠す)。</summary>
    void ShowVideoView()
    {
        if (videoView) return;
        StopClip();
        videoView = true;
        Picture.Visibility = Overlay.Visibility = PlayButton.Visibility = SpeedBox.Visibility = Visibility.Collapsed;
        SetStageSize(fullSize.Width, fullSize.Height);
        Player.Open(FullPath(session.VideoFile!), SliderTime); // 開くだけ。シークバーの位置で止まっている
        Player.Visibility = Visibility.Visible;
        if (fit) FitZoom();
    }

    void LeaveVideoView()
    {
        if (!videoView) return;
        SetFullPlaying(false);
        videoView = false;
        Player.Close();
        Player.Visibility = Visibility.Collapsed;
        Picture.Visibility = Overlay.Visibility = Visibility.Visible;
    }

    void SetFullPlaying(bool playing)
    {
        fullPlaying = playing;
        if (playing) { Player.Play(); StartPlayTimer(); }
        else if (videoView) Player.Pause();
        FullPlayButton.Content = playing ? "‖" : "▶";
    }

    /// <summary>タイムラインの目盛り(ステップを撮った時刻)を付け直す。</summary>
    void UpdateTicks() =>
        TimeSlider.Ticks = new DoubleCollection(session.Steps.Where(s => s.VideoTime != null).Select(s => s.VideoTime!.Value.TotalSeconds));

    TimeSpan SliderTime => TimeSpan.FromSeconds(TimeSlider.Value);

    /// <summary>シークバーを動かす(全編の表示には切り替えない)。</summary>
    void MoveSlider(TimeSpan t)
    {
        sliderQuiet = true;
        TimeSlider.Value = t.TotalSeconds;
        sliderQuiet = false;
    }

    void TimeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        TimeLabel.Text = TimeText.Position(SliderTime);
        if (!TimeSlider.IsMouseCaptureWithin) ShowPlayhead(); // ドラッグ中に動かすと、つまみがマウスから逃げる
        if (sliderQuiet) return;
        ShowVideoView();
        Player.Position = SliderTime;
    }

    // ---- タイムラインのズーム ----
    // シークバー自体の幅を広げて横にスクロールする。倍率 1 で全編が収まる

    const double MinVisibleSeconds = 1; // いちばん拡大したときに見える長さ
    double timeZoom = 1;

    /// <summary>シークバーの両端の、つまみの中心が届かない幅。</summary>
    double TrackInset() =>
        TimeSlider.Template.FindName("PART_Track", TimeSlider) is Track { Thumb: { } thumb } track
            ? track.TranslatePoint(new Point(), TimeSlider).X + thumb.ActualWidth / 2
            : 0;

    double PlayheadX() => Geometry.TrackX(TimeSlider.Value / Math.Max(TimeSlider.Maximum, double.Epsilon), TimeSlider.ActualWidth, TrackInset());

    /// <summary>倍率を変える。anchor(見えている範囲での X)の時刻は動かさない。省略時は現在位置、それが見えていなければ中央。</summary>
    void SetTimeZoom(double z, double? anchor = null)
    {
        var (offset, view, oldWidth) = (TimeScroller.HorizontalOffset, TimeScroller.ViewportWidth, TimeSlider.ActualWidth);
        var head = PlayheadX() - offset;
        var at = anchor ?? (head >= 0 && head <= view ? head : view / 2);
        timeZoom = Math.Clamp(z, 1, Math.Max(1, TimeSlider.Maximum / MinVisibleSeconds));
        ApplyTimeWidth();
        TimeScroller.UpdateLayout();
        TimeScroller.ScrollToHorizontalOffset(Geometry.ZoomScroll(offset, at, oldWidth, TimeSlider.ActualWidth, TrackInset()));
        TimeScroller.UpdateLayout(); // 続けて回されても、動かしたあとの位置から計算する
    }

    void ApplyTimeWidth() => TimeSlider.Width = timeZoom > 1 ? TimeScroller.ViewportWidth * timeZoom : double.NaN;

    void ShowPlayhead()
    {
        if (timeZoom <= 1) return;
        TimeScroller.ScrollToHorizontalOffset(Geometry.ScrollToShow(TimeScroller.HorizontalOffset, TimeScroller.ViewportWidth, PlayheadX()));
    }

    void TimeZoomIn_Click(object sender, RoutedEventArgs e) => SetTimeZoom(timeZoom * ZoomStep);
    void TimeZoomOut_Click(object sender, RoutedEventArgs e) => SetTimeZoom(timeZoom / ZoomStep);
    void TimeZoomFit_Click(object sender, RoutedEventArgs e) => SetTimeZoom(1);
    void TimeSlider_DragCompleted(object sender, DragCompletedEventArgs e) => ShowPlayhead();

    void TimeScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) ApplyTimeWidth();
    }

    /// <summary>Ctrl+ホイールでマウスの位置の時刻を中心に拡大縮小、ホイールだけなら横にスクロールする。</summary>
    void TimeScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (Keyboard.Modifiers == ModifierKeys.Control) SetTimeZoom(timeZoom * Math.Pow(ZoomStep, e.Delta / 120.0), e.GetPosition(TimeScroller).X);
        else TimeScroller.ScrollToHorizontalOffset(TimeScroller.HorizontalOffset - e.Delta);
    }

    void FullPlay_Click(object sender, RoutedEventArgs e)
    {
        ShowVideoView();
        SetFullPlaying(!fullPlaying);
    }

    void AddFrame_Click(object sender, RoutedEventArgs e)
    {
        var time = SliderTime;
        InsertFromVideo(CaptureTrigger.Manual, VideoClips.Frame(FullPath(session.VideoFile!), time), time, _ => { });
    }

    void RangeStart_Click(object sender, RoutedEventArgs e) { rangeStart = SliderTime; ShowRange(); }
    void RangeEnd_Click(object sender, RoutedEventArgs e) { rangeEnd = SliderTime; ShowRange(); }

    void ShowRange() =>
        RangeLabel.Text = $"{(rangeStart is { } a ? TimeText.Position(a) : "?")} 〜 {(rangeEnd is { } b ? TimeText.Position(b) : "?")}";

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

    async void ExportForDistribution_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "配布用に書き出すフォルダ", InitialDirectory = System.IO.Path.GetDirectoryName(dir) };
        if (dialog.ShowDialog(this) != true) return;
        var (format, mode, outDir) = ((ClipFormat)ClipFormatBox.SelectedIndex, (GroupMode)Group.SelectedIndex, dialog.FolderName);
        var settings = Settings.Load(Settings.DefaultPath);
        settings.ClipFormat = format;
        settings.Save(Settings.DefaultPath);

        // 動画はフレームを1枚ずつ取り出して作り直すので時間がかかる。裏で動かし、進み具合を出す
        var snapshot = SessionStore.Clone(session);
        var total = Export.Sections(snapshot, mode).SelectMany(sec => sec.Steps).Count(st => st.VideoFile != null);
        var progress = new ProgressWindow("配布用に書き出し中", "書き出しています...", this);
        var done = 0;
        void Report(double clip) => progress.Report($"動画を作っています({done + 1} / {total} 本目)", (done + clip) / total);
        IsEnabled = false;
        progress.Show();
        try
        {
            await Task.Run(() => Export.ExportForDistribution(snapshot, dir, outDir, mode, format, (st, clip, poster) =>
            {
                VideoClips.ExportClip(dir, st, format, clip, poster, Report);
                done++;
            }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, $"書き出せませんでした: {ex.Message}", "step_recorder", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally
        {
            progress.Finish();
            IsEnabled = true;
            Activate();
        }
        Process.Start(new ProcessStartInfo(System.IO.Path.Combine(outDir, Export.HtmlFile)) { UseShellExecute = true });
    }

    // ---- 表示欄を別ウインドウに出す ----

    Window? popup;

    void PopOut_Click(object sender, RoutedEventArgs e)
    {
        if (popup != null) { popup.Close(); return; } // 出ていればここへ戻す
        var back = new Button { Content = "表示欄をここに戻す", Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        back.Click += (_, _) => popup?.Close();
        PreviewSlot.Content = back; // 先に外してから別ウインドウに入れる(1つの部品は1か所にしか置けない)
        popup = new Window
        {
            Title = $"step_recorder プレビュー — {System.IO.Path.GetFileName(dir)}",
            Owner = this,
            Width = 1100, Height = 750,
            FontFamily = FontFamily, FontSize = FontSize,
            Content = PreviewBorder,
        };
        popup.PreviewKeyDown += Window_PreviewKeyDown; // Ctrl+Z やズームのキーも効くように
        popup.Closed += (_, _) =>
        {
            popup!.Content = null;
            PreviewSlot.Content = PreviewBorder;
            popup = null;
            PopOutButton.Content = "別ウインドウで表示";
        };
        popup.Show();
        PopOutButton.Content = "表示欄を戻す";
    }

    // ---- 保存 ----

    void Group_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        MarkDirty();
        RefreshList(CurrentIndex);
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
        var i = CurrentIndex;
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

    protected override void OnClosed(EventArgs e)
    {
        playTimer.Stop();
        Player.Close();
        base.OnClosed(e);
    }
}
