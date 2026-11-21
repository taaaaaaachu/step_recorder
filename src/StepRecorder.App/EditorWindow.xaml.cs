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
    readonly List<string> removedImages = [];
    bool dirty, loading;

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
        RefreshList(0);
    }

    // ---- 一覧 ----

    void RefreshList(int select)
    {
        Steps.ItemsSource = session.Steps.Select((s, i) => new StepView(s, Thumb(s), $"{i + 1}. {s.ScreenName}", s.Description)).ToList();
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
        (session.Steps[i], session.Steps[j]) = (session.Steps[j], session.Steps[i]);
        MarkDirty();
        RefreshList(j);
    }

    void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    void Down_Click(object sender, RoutedEventArgs e) => Move(1);

    void DeleteStep_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } s) return;
        var i = Steps.SelectedIndex;
        session.Steps.Remove(s);
        removedImages.Add(s.ImageFile); // 画像は保存したときに消す
        MarkDirty();
        RefreshList(i);
    }

    // ---- 文字の編集 ----

    void ScreenName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (loading || Current is not { } s) return;
        s.ScreenName = ScreenName.Text;
        MarkDirty();
    }

    void Description_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (loading || Current is not { } s) return;
        s.Description = Description.Text.ReplaceLineEndings("\n");
        MarkDirty();
    }

    void Field_LostFocus(object sender, RoutedEventArgs e) => RefreshList(Steps.SelectedIndex);

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
                if (ToolCrop.IsChecked == true) s.Crop = preview;
                else { s.Highlights.Add(preview); selectedHighlight = s.Highlights.Count - 1; }
                MarkDirty();
            }
            else if (drag is Drag.Move or Drag.Resize && preview != dragOrigin)
            {
                s.Highlights[selectedHighlight] = preview;
                MarkDirty();
            }
        }
        drag = Drag.None;
        preview = null;
        Redraw();
    }

    void DeleteHighlight_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } s || selectedHighlight < 0 || selectedHighlight >= s.Highlights.Count) return;
        s.Highlights.RemoveAt(selectedHighlight);
        selectedHighlight = -1;
        MarkDirty();
        Redraw();
    }

    void ClearCrop_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { Crop: not null } s) return;
        s.Crop = null;
        MarkDirty();
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
        if (Keyboard.FocusedElement is TextBox) return;
        if (e.Key == Key.Delete) { DeleteHighlight_Click(this, new()); e.Handled = true; }
        if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.Up or Key.Down)
        {
            Move(e.SystemKey == Key.Up ? -1 : 1);
            e.Handled = true;
        }
    }

    // ---- 保存 ----

    void Group_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!loading) MarkDirty();
    }

    void MarkDirty() => dirty = true;

    bool Save()
    {
        try
        {
            Export.SaveAll(session, dir, (GroupMode)Group.SelectedIndex);
            foreach (var f in removedImages.Where(f => session.Steps.All(s => s.ImageFile != f)))
                File.Delete(System.IO.Path.Combine(dir, f));
            removedImages.Clear();
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
