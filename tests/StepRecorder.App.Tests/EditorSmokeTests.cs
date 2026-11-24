using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StepRecorder.Core;
using static StepRecorder.App.Tests.Desktop;

namespace StepRecorder.App.Tests;

public sealed class EditorSmokeTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "sr-editor-" + Guid.NewGuid());
    Process? app;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint GetDpiForWindow(nint hwnd);

    public void Dispose()
    {
        try { app?.Kill(); app?.WaitForExit(5000); } catch (InvalidOperationException) { }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    /// <summary>記録フォルダを渡して起動すると、開始画面を出さずに編集画面が開く。</summary>
    [E2EFact]
    public void 記録フォルダを渡すと編集画面が開く()
    {
        var rec = new Recording(new Session { TargetName = "Demo", CreatedAt = DateTimeOffset.Now });
        for (var i = 0; i < 2; i++)
        {
            var file = rec.NextImageFile;
            WindowCapture.SavePng(Solid(400, 300, i == 0 ? Colors.SteelBlue : Colors.SeaGreen), Path.Combine(dir, file));
            var st = rec.AddStep(CaptureTrigger.Manual, new($"画面{i + 1}", "W", "Demo"), file, 400, 300, DateTimeOffset.Now);
            st.Description = $"説明{i + 1}";
            st.Highlights.Add(new(50, 50, 120, 60));
        }
        rec.Session.Steps[0].Crop = new(20, 20, 300, 200);
        SessionStore.Save(rec.Session, dir);

        app = Launch(Path.Combine(AppContext.BaseDirectory, "StepRecorder.exe"), $"\"{dir}\"");
        var editor = app.MainWindowHandle;
        Assert.StartsWith("step_recorder 編集", Native.Text(editor));
        Wait(500); // 描画を待つ

        // 動画の形式の選択肢が見切れない
        var format = Find(editor, "ClipFormatBox");
        var expand = (ExpandCollapsePattern)format.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        foreach (var name in new[] { "GIF", "MP4" })
        {
            expand.Expand();
            Wait(300);
            var item = format.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new PropertyCondition(AutomationElement.NameProperty, name)));
            ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            expand.Collapse();
            Wait(500);
            if (Environment.GetEnvironmentVariable("STEP_RECORDER_E2E_OUT") is { Length: > 0 } o)
                WindowCapture.SavePng(WindowCapture.Capture(editor)!.Image, Path.Combine(o, $"format-{name}.png"));
            var textWidth = new FormattedText(name, System.Globalization.CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                new Typeface("Yu Gothic UI"), 13, Brushes.Black, 1).Width;
            var width = format.Current.BoundingRectangle.Width / (GetDpiForWindow(editor) / 96.0);
            // 選択中の文字は UI Automation に出ないので欄の幅で見る。50 は Fluent の ComboBox の左右の余白と矢印の分
            Assert.True(width >= textWidth + 50, $"{name} が見切れる: 欄 {width:0.#} / 文字 {textWidth:0.#} + 50");
        }

        var shot = WindowCapture.Capture(editor);
        Assert.NotNull(shot);
        Assert.True(shot.Image.PixelWidth > 600);
        if (Environment.GetEnvironmentVariable("STEP_RECORDER_E2E_OUT") is { Length: > 0 } outDir)
            WindowCapture.SavePng(shot.Image, Path.Combine(outDir, "editor.png"));

        // ステップを削除して、Ctrl+Z で元に戻す
        int Count() => Find(editor, "Steps").FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Count;
        Assert.Equal(2, Count());
        ClickOn(editor, new PropertyCondition(AutomationElement.NameProperty, "削除"));
        Wait(300);
        Assert.Equal(1, Count());
        PressKeys(editor, 0x11, 0x5A); // Ctrl+Z
        Wait(300);
        Assert.Equal(2, Count());

        // 一覧のステップを選んで Delete。続けて押しても消える
        ClickOn(editor, new PropertyCondition(AutomationElement.NameProperty, "1. 画面1")); // 一覧の見出し
        Wait(300);
        PressKeys(editor, 0x2E);
        Wait(300);
        Assert.Equal(1, Count());
        PressKeys(editor, 0x2E);
        Wait(300);
        Assert.Equal(0, Count());
        PressKeys(editor, 0x11, 0x5A);
        PressKeys(editor, 0x11, 0x5A);
        Wait(300);
        Assert.Equal(2, Count());
        PressKeys(editor, 0x11, 0x59); // Ctrl+Y でやり直す
        Wait(300);
        Assert.Equal(1, Count());
        ClickOn(editor, new PropertyCondition(AutomationElement.NameProperty, "やり直す"));
        Wait(300);
        Assert.Equal(0, Count());
        ClickOn(editor, new PropertyCondition(AutomationElement.NameProperty, "元に戻す"));
        Wait(300);
        Assert.Equal(1, Count());
    }

    [E2EFact]
    public void 編集画面はズームできる()
    {
        var rec = new Recording(new Session { TargetName = "Demo", CreatedAt = DateTimeOffset.Now });
        var file = rec.NextImageFile;
        WindowCapture.SavePng(Solid(3000, 2000, Colors.SteelBlue), Path.Combine(dir, file)); // 表示欄より大きい画像
        rec.AddStep(CaptureTrigger.Manual, new("大きい画面", "W", "Demo"), file, 3000, 2000, DateTimeOffset.Now);
        SessionStore.Save(rec.Session, dir);

        app = Launch(Path.Combine(AppContext.BaseDirectory, "StepRecorder.exe"), $"\"{dir}\"");
        var editor = app.MainWindowHandle;
        int Percent() => int.Parse(Find(editor, "ZoomLabel").Current.Name.TrimEnd('%'));
        Wait(500);

        var fitted = Percent();
        Assert.InRange(fitted, 10, 99); // 最初は全体が収まる大きさ

        ClickOn(editor, new PropertyCondition(AutomationElement.NameProperty, "+"));
        Wait(300);
        Assert.InRange(Percent(), fitted * 1.25 - 1, fitted * 1.25 + 1);

        var r = Find(editor, "Scroller").Current.BoundingRectangle;
        var before = Percent();
        CtrlWheel(editor, (int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2), 1);
        Wait(300);
        Assert.True(Percent() > before, $"Ctrl+ホイールで拡大されない: {before}% → {Percent()}%");

        ClickOn(editor, new PropertyCondition(AutomationElement.NameProperty, "100%"));
        Wait(300);
        Assert.Equal(100, Percent());

        // ホイールボタンを押したまま左上へドラッグすると、画像が左上へ動く(スクロール位置は右下へ)
        var scroll = (ScrollPattern)Find(editor, "Scroller").GetCurrentPattern(ScrollPattern.Pattern);
        var (h0, v0) = (scroll.Current.HorizontalScrollPercent, scroll.Current.VerticalScrollPercent);
        var (cx, cy) = ((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
        MiddleDrag(editor, cx, cy, cx - 200, cy - 150);
        Wait(300);
        Assert.True(scroll.Current.HorizontalScrollPercent > h0 && scroll.Current.VerticalScrollPercent > v0,
            $"ドラッグで動かない: ({h0:0.#}, {v0:0.#}) → ({scroll.Current.HorizontalScrollPercent:0.#}, {scroll.Current.VerticalScrollPercent:0.#})");
    }


    /// <summary>キーボードの LANG1/LANG2(VK_IME_ON/OFF・VK_KANA/HANJA)で、説明欄の IME がオン・オフになる。</summary>
    [E2EFact]
    public void 説明欄でLANG1とLANG2がIMEを切り替える()
    {
        var rec = new Recording(new Session { TargetName = "Demo", CreatedAt = DateTimeOffset.Now });
        var file = rec.NextImageFile;
        WindowCapture.SavePng(Solid(400, 300, Colors.SteelBlue), Path.Combine(dir, file));
        rec.AddStep(CaptureTrigger.Manual, new("画面", "W", "Demo"), file, 400, 300, DateTimeOffset.Now);
        SessionStore.Save(rec.Session, dir);

        app = Launch(Path.Combine(AppContext.BaseDirectory, "StepRecorder.exe"), $"\"{dir}\"");
        var editor = app.MainWindowHandle;
        Wait(500);
        ClickOn(editor, "Description");
        Wait(300);

        foreach (var (on, off) in new (ushort, ushort)[] { (0x16, 0x1A), (0x15, 0x19) })
        {
            PressKeys(editor, on);
            Wait(300);
            Assert.True(Native.ImeOpen(editor), $"0x{on:X2} で IME がオンにならない");
            PressKeys(editor, off);
            Wait(300);
            Assert.False(Native.ImeOpen(editor), $"0x{off:X2} で IME がオフにならない");
        }
    }



    /// <summary>上の段のボタンとドロップダウン(出力の並べ方・動画の形式)は高さが揃う。</summary>
    [DesktopFact]
    public void 上の段の部品は高さが揃う()
    {
        var rec = new Recording(new Session { TargetName = "Demo", CreatedAt = DateTimeOffset.Now });
        WindowCapture.SavePng(Solid(400, 300, Colors.SteelBlue), Path.Combine(dir, rec.NextImageFile));
        rec.AddStep(CaptureTrigger.Manual, new("画面", "W", "Demo"), rec.NextImageFile, 400, 300, DateTimeOffset.Now);
        SessionStore.Save(rec.Session, dir);
        app = Open(Path.Combine(AppContext.BaseDirectory, "StepRecorder.exe"), $"\"{dir}\"");
        var editor = app.MainWindowHandle;
        Wait(800);

        var heights = new[] { "Group", "UndoButton", "RedoButton", "SaveButton", "ExportButton", "ExportMenuButton", "ClipFormatBox" }
            .ToDictionary(id => id, id => Math.Round(Find(editor, id).Current.BoundingRectangle.Height));
        if (Environment.GetEnvironmentVariable("STEP_RECORDER_E2E_OUT") is { Length: > 0 } o)
            WindowCapture.SavePng(WindowCapture.Capture(editor)!.Image, Path.Combine(o, "editor-toolbar.png"));
        Assert.True(heights.Values.Distinct().Count() == 1, string.Join(", ", heights.Select(h => $"{h.Key}={h.Value}")));
    }

    /// <summary>配布用に書き出すボタンの本体はそのままフォルダを選ぶ画面を出し、横の矢印は書き出し方のメニューを出す。</summary>
    [E2EFact]
    public void 配布用に書き出すの本体はフォルダに書き出し_矢印でHTML1ファイルを選べる()
    {
        var rec = new Recording(new Session { TargetName = "Demo", CreatedAt = DateTimeOffset.Now });
        WindowCapture.SavePng(Solid(400, 300, Colors.SteelBlue), Path.Combine(dir, rec.NextImageFile));
        rec.AddStep(CaptureTrigger.Manual, new("画面", "W", "Demo"), rec.NextImageFile, 400, 300, DateTimeOffset.Now);
        SessionStore.Save(rec.Session, dir);
        app = Launch(Path.Combine(AppContext.BaseDirectory, "StepRecorder.exe"), $"\"{dir}\"");
        var editor = app.MainWindowHandle;
        Wait(500);
        // ダイアログとメニューは別のウインドウに出るので、このプロセスのものをデスクトップ全体から探す
        AutomationElement? Top(ControlType type, string name) => AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id),
            new PropertyCondition(AutomationElement.ControlTypeProperty, type), new PropertyCondition(AutomationElement.NameProperty, name)));
        void Invoke(string id) => ((InvokePattern)Find(editor, id).GetCurrentPattern(InvokePattern.Pattern)).Invoke();

        Invoke("ExportButton");
        AutomationElement? dialog = null;
        Assert.True(Until(() => (dialog = Top(ControlType.Window, "配布用に書き出すフォルダ")) != null), "本体を押してもフォルダを選ぶ画面が出ない");
        Assert.Null(Top(ControlType.MenuItem, "HTML 1 ファイルに書き出す...")); // メニューは出さない
        var cancel = dialog!.FindFirst(TreeScope.Children, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.NameProperty, "キャンセル")));
        ((InvokePattern)cancel.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        Assert.True(Until(() => Top(ControlType.Window, "配布用に書き出すフォルダ") == null), "フォルダを選ぶ画面が閉じない");

        Invoke("ExportMenuButton");
        Assert.True(Until(() => Top(ControlType.MenuItem, "HTML 1 ファイルに書き出す...") != null), "矢印を押しても書き出し方のメニューが出ない");
        Assert.NotNull(Top(ControlType.MenuItem, "フォルダに書き出す..."));
    }

    /// <summary>表示欄だけを別ウインドウに出し、閉じると編集画面に戻る。</summary>
    [E2EFact]
    public void 表示欄を別ウインドウに出して戻せる()
    {
        var rec = new Recording(new Session { TargetName = "Demo", CreatedAt = DateTimeOffset.Now });
        WindowCapture.SavePng(Solid(400, 300, Colors.SteelBlue), Path.Combine(dir, rec.NextImageFile));
        rec.AddStep(CaptureTrigger.Manual, new("画面", "W", "Demo"), rec.NextImageFile, 400, 300, DateTimeOffset.Now);
        SessionStore.Save(rec.Session, dir);
        app = Launch(Path.Combine(AppContext.BaseDirectory, "StepRecorder.exe"), $"\"{dir}\"");
        var editor = app.MainWindowHandle;
        Wait(500);

        // WPF の子ウインドウ(Owner 付き)は UI Automation では編集画面の下に出るので、表示欄がどちらのウインドウにあるかは親をたどって見る
        string? WindowOfScroller()
        {
            var e = AutomationElement.FromHandle(editor).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "Scroller"));
            for (; e != null; e = TreeWalker.ControlViewWalker.GetParent(e))
                if (e.Current.ControlType == ControlType.Window) return e.Current.Name;
            return null;
        }
        var popupTitle = $"step_recorder プレビュー — {Path.GetFileName(dir)}";

        Assert.StartsWith("step_recorder 編集", WindowOfScroller());
        ClickOn(editor, new PropertyCondition(AutomationElement.NameProperty, "別ウインドウで表示"));
        Assert.True(Until(() => WindowOfScroller() == popupTitle), "表示欄が別ウインドウに出ない");

        // F・F11 でそのウインドウがある画面いっぱいになり、F・F11・Esc で戻る
        var popup = (nint)Find(editor, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
            new PropertyCondition(AutomationElement.NameProperty, popupTitle))).Current.NativeWindowHandle;
        System.Windows.Rect Bounds() => AutomationElement.FromHandle(popup).Current.BoundingRectangle;
        bool FullScreen() => Monitors.All().Any(m => Bounds() == new System.Windows.Rect(m.Bounds.X, m.Bounds.Y, m.Bounds.Width, m.Bounds.Height));
        var normal = Bounds();
        foreach (var (on, off) in new (ushort, ushort)[] { (0x7A, 0x1B), (0x46, 0x7A), (0x7A, 0x46) }) // F11→Esc、F→F11、F11→F
        {
            PressKeys(popup, on);
            Assert.True(Until(FullScreen), $"キー {on:X} で全画面にならない: {Bounds()} / 画面 {string.Join(", ", Monitors.All().Select(m => m.Bounds))}");
            PressKeys(popup, off);
            Assert.True(Until(() => Bounds() == normal), $"キー {off:X} で元の大きさに戻らない");
        }

        // 別ウインドウが編集画面の上に重なっていることがあるので、マウスではなく UI Automation で押す
        ((InvokePattern)Find(editor, new PropertyCondition(AutomationElement.NameProperty, "表示欄をここに戻す")).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        Assert.True(Until(() => WindowOfScroller()?.StartsWith("step_recorder 編集") == true), "表示欄が戻らない");
    }

    /// <summary>画面一覧モードにすると、一覧も出力と同じく画面ごとに区切られる。</summary>
    [E2EFact]
    public void 画面一覧モードでは一覧を画面ごとに区切る()
    {
        var rec = new Recording(new Session { TargetName = "Demo", CreatedAt = DateTimeOffset.Now });
        foreach (var name in new[] { "画面A", "画面B", "画面A" })
        {
            var file = rec.NextImageFile;
            WindowCapture.SavePng(Solid(200, 100, Colors.SteelBlue), Path.Combine(dir, file));
            rec.AddStep(CaptureTrigger.Manual, new(name, "W", "Demo"), file, 200, 100, DateTimeOffset.Now);
        }
        SessionStore.Save(rec.Session, dir);
        app = Launch(Path.Combine(AppContext.BaseDirectory, "StepRecorder.exe"), $"\"{dir}\"");
        var editor = app.MainWindowHandle;
        Wait(500);

        List<string> Headers() => Find(editor, "Steps").FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>().Select(e => e.Current.Name).Where(n => n.StartsWith("画面") || n.Length > 0 && char.IsDigit(n[0])).ToList();
        void Choose(string item)
        {
            var group = Find(editor, "Group");
            ((ExpandCollapsePattern)group.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
            Wait(300);
            ((SelectionItemPattern)group.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, item))
                .GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            ((ExpandCollapsePattern)group.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse();
            Wait(500);
        }

        int Groups() => Find(editor, "Steps").FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group)).Count;
        Choose("手順モード(撮った順)");
        Assert.Equal(["1. 画面A", "2. 画面B", "3. 画面A"], Headers());
        Assert.Equal(0, Groups());
        Choose("画面一覧モード(画面ごと)");
        Assert.Equal(["1. 画面A", "3. 画面A", "2. 画面B"], Headers()); // 出力と同じく、同じ画面がまとまる
        Assert.Equal(2, Groups());
        if (Environment.GetEnvironmentVariable("STEP_RECORDER_E2E_OUT") is { Length: > 0 } shotDir)
            WindowCapture.SavePng(WindowCapture.Capture(editor)!.Image, Path.Combine(shotDir, "editor-byscreen.png"));
    }

    [E2EFact]
    public async Task クリックの無い録画の記録を開くと動画が出て_フレームを足せる()
    {
        var px = new byte[320 * 240 * 4];
        Array.Fill(px, (byte)120);
        var nv12 = Nv12.FromBgra(px, 320, 240);
        await VideoRecorder.Encode(Path.Combine(dir, "video.mp4"), 320, 240, Enumerable.Range(0, 30).Select(i => (nv12, TimeSpan.FromSeconds(i / 15.0))));
        SessionStore.Save(new Session { TargetName = "Demo", CreatedAt = DateTimeOffset.Now, VideoFile = "video.mp4" }, dir);

        app = Launch(Path.Combine(AppContext.BaseDirectory, "StepRecorder.exe"), $"\"{dir}\"");
        var editor = app.MainWindowHandle;
        Wait(1000);
        var shot = VideoE2ETests.Bgr24(WindowCapture.Capture(editor)!.Image);
        Assert.True(Enumerable.Range(0, shot.Length / 3).Count(i => Math.Abs(shot[i * 3] - 120) <= 3 && Math.Abs(shot[i * 3 + 2] - 120) <= 3) > 10000, "動画が表示されていない");
        ClickOn(editor, new PropertyCondition(AutomationElement.NameProperty, "このフレームをステップに追加"));
        Wait(800);
        PressKeys(editor, 0x11, 0x53); // Ctrl+S
        Assert.True(Until(() => SessionStore.Load(dir).Steps.Count == 1));
    }

    [E2EFact]
    public async Task ステップの無い録画のシークバーには途中の目盛りが出ない()
    {
        var nv12 = Nv12.FromBgra(new byte[320 * 240 * 4], 320, 240);
        await VideoRecorder.Encode(Path.Combine(dir, "video.mp4"), 320, 240, Enumerable.Range(0, 30).Select(i => (nv12, TimeSpan.FromSeconds(i / 15.0))));
        SessionStore.Save(new Session { TargetName = "Demo", CreatedAt = DateTimeOffset.Now, VideoFile = "video.mp4" }, dir);

        app = Launch(Path.Combine(AppContext.BaseDirectory, "StepRecorder.exe"), $"\"{dir}\"");
        var editor = app.MainWindowHandle;
        Wait(1000);
        var shot = WindowCapture.Capture(editor)!;
        var r = Find(editor, "TimeSlider").Current.BoundingRectangle;
        // 2 秒の動画なので、1 秒おきの目盛りなら真ん中に出る。つまみは左端、溝は横線なので、真ん中の列が左右より暗ければ目盛り
        var (px, width) = (VideoE2ETests.Bgr24(shot.Image), shot.Image.PixelWidth);
        int At(int x, int y) => px[(y * width + x) * 3 + 1];
        var mid = (int)(r.X + r.Width / 2) - shot.Bounds.Left;
        for (var y = (int)r.Y - shot.Bounds.Top; y < (int)r.Bottom - shot.Bounds.Top; y++)
            for (var x = mid - 10; x <= mid + 10; x++)
                Assert.True(At(x, y) > Math.Min(At(mid - 30, y), At(mid + 30, y)) - 40, $"途中に目盛りがある({x},{y})");
    }

    /// <summary>録画ボタンで録った動画のステップは、シークバーで開始・終了を変えたり2つに分けたりできる。入力は送らず UI Automation で押す。</summary>
    [DesktopFact]
    public async Task 録画ボタンの動画のステップは区間を変えて分割できる()
    {
        // 3 秒の動画。コマごとに明るさを変え、ポスターがどの時刻のものか分かるようにする
        Directory.CreateDirectory(Path.Combine(dir, "videos"));
        var video = Path.Combine(dir, "videos", "0001.mp4");
        await VideoRecorder.Encode(video, 320, 240, Enumerable.Range(0, 45).Select(i =>
        {
            var px = new byte[320 * 240 * 4];
            Array.Fill(px, (byte)(20 + i * 4));
            return (Nv12.FromBgra(px, 320, 240), TimeSpan.FromSeconds(i / 15.0));
        }));
        var rec = new Recording(new Session { TargetName = "Demo", CreatedAt = DateTimeOffset.Now });
        WindowCapture.SavePng(VideoClips.Frame(video, TimeSpan.Zero), Path.Combine(dir, rec.NextImageFile));
        var st = rec.AddStep(CaptureTrigger.Video, new("画面", "W", "Demo"), rec.NextImageFile, 320, 240, DateTimeOffset.Now);
        st.VideoFile = "videos/0001.mp4";
        foreach (var (field, at) in new[] { ("氏名", 1.0), ("住所", 2.0) })
        {
            rec.AddOperation(new() { Kind = OperationKind.TextInput, Element = new(field, "Edit", field, null), Text = "x" });
            st.Operations[^1].VideoTime = TimeSpan.FromSeconds(at);
        }
        SessionStore.Save(rec.Session, dir);

        app = Open(Path.Combine(AppContext.BaseDirectory, "StepRecorder.exe"), $"\"{dir}\"");
        var editor = app.MainWindowHandle;
        var slider = (RangeValuePattern)Find(editor, "ClipSlider").GetCurrentPattern(RangeValuePattern.Pattern);
        Assert.InRange(slider.Current.Maximum, 2.8, 3.2);
        void Press(string name)
        {
            ((InvokePattern)Find(editor, new PropertyCondition(AutomationElement.NameProperty, name)).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            Wait(500);
        }
        void At(double sec) { slider.SetValue(sec); Wait(300); }
        int Count() => Find(editor, "Steps").FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Count;

        At(0.4);
        Press("開始位置");
        At(2.6);
        Press("終了位置");
        Assert.Equal("使う区間 0:00.4 〜 0:02.6", Find(editor, "ClipRangeLabel").Current.Name);
        At(1.6);
        if (Environment.GetEnvironmentVariable("STEP_RECORDER_E2E_OUT") is { Length: > 0 } o)
            WindowCapture.SavePng(WindowCapture.Capture(editor)!.Image, Path.Combine(o, "editor-clip.png"));
        Press("分割");
        Assert.Equal(2, Count());
        Assert.InRange(slider.Current.Value, 1.5, 1.7); // 後ろのステップを選び、その開始にいる
        Press("元に戻す");
        Assert.Equal(1, Count());
        Press("やり直す");
        Assert.Equal(2, Count());
        Press("保存");

        var saved = SessionStore.Load(dir);
        double R(TimeSpan? t) => Math.Round(t!.Value.TotalSeconds, 1);
        Assert.Equal((0.4, 1.6), (R(saved.Steps[0].VideoStart), R(saved.Steps[0].VideoEnd)));
        Assert.Equal((1.6, 2.6), (R(saved.Steps[1].VideoStart), R(saved.Steps[1].VideoEnd)));
        Assert.Equal(["『氏名』欄に「x」と入力", "『住所』欄に「x」と入力"], saved.Steps.Select(s => s.Description));
        // ポスターはそれぞれの区間の最初のフレーム(使わなくなった最初のポスターは保存で消える)
        Assert.False(File.Exists(Path.Combine(dir, "images", "0001.png")));
        foreach (var (s, at) in saved.Steps.Zip([0.4, 1.6]))
        {
            var poster = BitmapFrame.Create(new Uri(Path.Combine(dir, s.ImageFile)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad); // ファイルを掴んだままにしない
            var px = new byte[4];
            new FormatConvertedBitmap(poster, PixelFormats.Bgra32, null, 0).CopyPixels(new System.Windows.Int32Rect(10, 10, 1, 1), px, 4, 0);
            Assert.InRange(px[1], 20 + at * 15 * 4 - 6, 20 + at * 15 * 4 + 6);
        }
    }

    /// <summary>録画モードの記録を開くと、全編の動画からフレームと区間をステップに足せる。配布用にも書き出せる。</summary>
    [E2EFact]
    public void 録画モードの記録では全編からフレームと区間を足せる()
    {
        // 録画モードの記録を作る(サンプルアプリを3秒録画し、0.5 秒の時点のステップを1つ持つ)
        var sample = Launch(SampleExe);
        try
        {
            var rec = VideoRecorder.Start(sample.MainWindowHandle, Path.Combine(dir, "video.mp4"), cursor: false);
            Wait(3000);
            rec.Stop();
        }
        finally { sample.Kill(); }
        var poster = VideoClips.Frame(Path.Combine(dir, "video.mp4"), TimeSpan.FromSeconds(0.5));
        var recording = new Recording(new Session { TargetName = "SampleTarget", CreatedAt = DateTimeOffset.Now, VideoFile = "video.mp4" });
        WindowCapture.SavePng(poster, Path.Combine(dir, recording.NextImageFile));
        var first = recording.AddStep(CaptureTrigger.Click, new("サンプル画面", "W", "SampleTarget"), recording.NextImageFile, poster.PixelWidth, poster.PixelHeight, DateTimeOffset.Now);
        first.VideoTime = TimeSpan.FromSeconds(0.5);
        SessionStore.Save(recording.Session, dir);

        app = Launch(Path.Combine(AppContext.BaseDirectory, "StepRecorder.exe"), $"\"{dir}\"");
        var editor = app.MainWindowHandle;
        Wait(1000);
        var slider = (RangeValuePattern)Find(editor, "TimeSlider").GetCurrentPattern(RangeValuePattern.Pattern);
        Until(() => slider.Current.Maximum > 1); // 動画を開き終えると長さが分かる
        Assert.InRange(slider.Current.Maximum, 2.5, 3.5); // 全編の長さ
        void At(double sec) { slider.SetValue(sec); Wait(300); }
        void Press(string name) { ClickOn(editor, new PropertyCondition(AutomationElement.NameProperty, name)); Wait(800); }

        // タイムラインのズーム。同じ場所をクリックして、マウスの下の時刻が動いていないことを確かめる
        var timeline = Find(editor, "TimeScroller");
        var timeScroll = (ScrollPattern)timeline.GetCurrentPattern(ScrollPattern.Pattern);
        double Shown() => timeScroll.Current.HorizontallyScrollable ? timeScroll.Current.HorizontalViewSize : 100;
        double Scrolled() => timeScroll.Current.HorizontalScrollPercent;
        void Shot(string name)
        {
            if (Environment.GetEnvironmentVariable("STEP_RECORDER_E2E_OUT") is { Length: > 0 } o)
                WindowCapture.SavePng(WindowCapture.Capture(editor)!.Image, Path.Combine(o, name + ".png"));
        }
        Assert.Equal(100, Shown());
        Shot("timeline-fit");
        var bar = timeline.Current.BoundingRectangle;
        var track = Find(editor, "TimeSlider").Current.BoundingRectangle;
        // シークバーは左右いっぱい、ズームのボタンはその下の段の右端
        var play = Find(editor, "FullPlayButton").Current.BoundingRectangle;
        var all = Find(editor, new PropertyCondition(AutomationElement.NameProperty, "タイムライン全体")).Current.BoundingRectangle;
        Assert.InRange(all.Y, play.Y - 1, play.Y + 1);
        Assert.InRange(bar.Left, play.Left - 2, play.Left + 2);
        Assert.InRange(bar.Right, all.Right - 2, all.Right + 2);
        Assert.True(bar.Bottom <= all.Top, "ズームのボタンがシークバーの段にある");
        var (x, y) = ((int)(bar.X + bar.Width * 0.7), (int)(track.Y + track.Height / 2));
        Click(editor, x, y);
        Wait(500);
        var pointed = slider.Current.Value;
        Assert.InRange(pointed, 1.5, 2.6);
        // クリックでシークバーにフォーカスが移るので、←→ でコマ送りできる
        var frame = Math.Floor(pointed * 15 + 1e-6);
        PressKeys(editor, 0x27); // →
        Wait(300);
        Assert.Equal((frame + 1) / 15, slider.Current.Value, 6);
        PressKeys(editor, 0x25); PressKeys(editor, 0x25); // ←←
        Wait(300);
        Assert.Equal((frame - 1) / 15, slider.Current.Value, 6);
        CtrlWheel(editor, x, y, 1);
        Wait(300);
        CtrlWheel(editor, x, y, 1);
        Wait(500);
        Assert.InRange(Shown(), 62, 66); // 1.25 倍を 2 回
        Click(editor, x, y);
        Wait(500);
        Assert.InRange(slider.Current.Value, pointed - 0.02, pointed + 0.02);

        for (var i = 0; i < 6; i++) Press("タイムラインを拡大");
        Assert.InRange(Shown(), 100 / slider.Current.Maximum - 1, 100 / slider.Current.Maximum + 1); // 1 秒分までで止まる
        At(0.05);
        Assert.InRange(Scrolled(), 0, 5); // 現在位置が見える所へ動く
        Shot("timeline-zoom");
        Press("再生");
        Assert.True(Until(() => Scrolled() > 10, 8000), "再生中に現在位置を追わない");
        if (AutomationElement.FromHandle(editor).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "一時停止")) != null) Press("一時停止");
        At(slider.Current.Maximum - 0.05);
        Assert.InRange(Scrolled(), 95, 100);
        Shot("timeline-end");
        Press("タイムラインを縮小");
        Assert.InRange(Shown(), 100 / slider.Current.Maximum + 2, 99);
        Press("タイムライン全体");
        Assert.Equal(100, Shown());

        At(2.0);
        if (Environment.GetEnvironmentVariable("STEP_RECORDER_E2E_OUT") is { Length: > 0 } seekDir) // シークすると、メインの表示欄に全編の動画が出る
        {
            Wait(500);
            WindowCapture.SavePng(WindowCapture.Capture(editor)!.Image, Path.Combine(seekDir, "editor-seek.png"));
        }
        Press("このフレームをステップに追加");
        At(1.0);
        Press("区間の開始");
        At(1.8);
        Press("区間の終了");
        var range = Find(editor, "RangeLabel").Current.BoundingRectangle;
        Assert.InRange(range.Y + range.Height / 2, play.Y, play.Bottom); // 区間を出しても同じ段に収まる
        Shot("timeline-range");
        Press("区間を動画ステップに追加");
        // 足した動画のステップが選ばれているので、再生できる(区間は 0.8 秒なので、押した直後から確かめる)
        ClickOn(editor, new PropertyCondition(AutomationElement.NameProperty, "▶ 再生"));
        Assert.True(Until(() => AutomationElement.FromHandle(editor).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "■ 停止")) != null), "再生が始まらない");
        if (Environment.GetEnvironmentVariable("STEP_RECORDER_E2E_OUT") is { Length: > 0 } shotDir)
        {
            Wait(500);
            WindowCapture.SavePng(WindowCapture.Capture(editor)!.Image, Path.Combine(shotDir, "editor-video.png"));
        }
        // 0.8 秒の区間を終えると、自分で止まる
        Assert.True(Until(() => AutomationElement.FromHandle(editor).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "▶ 再生")) != null, 4000), "区間の終わりで止まらない");
        // 速さを変えられて、元に戻せて、保存される
        var speedBox = Find(editor, "SpeedBox");
        void ChooseSpeed(string label)
        {
            var expand = (ExpandCollapsePattern)speedBox.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            expand.Expand();
            Wait(300);
            ((SelectionItemPattern)speedBox.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new PropertyCondition(AutomationElement.NameProperty, label))).GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            expand.Collapse();
            Wait(600);
        }
        string SelectedSpeed() => ((SelectionPattern)speedBox.GetCurrentPattern(SelectionPattern.Pattern)).Current.GetSelection()[0].Current.Name;
        bool Listed(string mark) => Find(editor, "Steps").FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>().Any(t => t.Current.Name.Contains(mark));

        ChooseSpeed("0.25 倍速");
        Assert.True(Listed("▶ 0.25 倍速"), "一覧に速さが付かない");
        Shot("editor-speed");
        // 0.8 秒の区間が 3.2 秒かかる
        Press("▶ 再生");
        Wait(1000);
        Assert.NotNull(AutomationElement.FromHandle(editor).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "■ 停止")));
        Assert.True(Until(() => AutomationElement.FromHandle(editor).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "▶ 再生")) != null, 5000), "スローの再生が終わらない");
        Press("元に戻す");
        Assert.Equal("等速", SelectedSpeed());
        Assert.False(Listed("▶ 0.25 倍速"), "元に戻しても一覧に速さが残る");
        ChooseSpeed("2 倍速");

        PressKeys(editor, 0x11, 0x53); // Ctrl+S
        Wait(800);

        var saved = SessionStore.Load(dir);
        Assert.Equal(2, saved.Steps[1].Speed);
        Assert.Equal([0.5, 1.0, 2.0], saved.Steps.Select(s => Math.Round(s.VideoTime!.Value.TotalSeconds, 1))); // 時刻の順に並ぶ
        var clip = saved.Steps[1];
        Assert.Equal(("video.mp4", CaptureTrigger.Video), (clip.VideoFile, clip.Trigger));
        Assert.Equal((1.0, 1.8), (Math.Round(clip.VideoStart!.Value.TotalSeconds, 1), Math.Round(clip.VideoEnd!.Value.TotalSeconds, 1)));
        Assert.All(saved.Steps, s => Assert.True(File.Exists(Path.Combine(dir, s.ImageFile))));

        // 配布用の書き出し(mp4 と GIF)。トリミングも反映される
        clip.Crop = new(0, 0, 200, 100);
        foreach (var format in new[] { ClipFormat.Mp4, ClipFormat.Gif })
        {
            var outDir = Path.Combine(dir, "dist-" + format);
            var reported = new List<double>();
            Export.ExportForDistribution(saved, dir, outDir, GroupMode.Sequence, format, (st, c, p) => VideoClips.ExportClip(dir, st, format, c, p, KeyOverlay.StrokesFor(saved, st), MouseOverlay.StrokesFor(saved, st), reported.Add));
            Assert.NotEmpty(reported); // 進み具合(0〜1)が増えながら届く
            Assert.All(reported, v => Assert.InRange(v, 0, 1));
            Assert.Equal(reported.Order(), reported);
            Assert.False(File.Exists(Path.Combine(outDir, SessionStore.FileName)));
            Assert.False(File.Exists(Path.Combine(outDir, "video.mp4")));
            if (format == ClipFormat.Mp4)
            {
                var (duration, w, h) = VideoE2ETests.VideoInfo(Path.Combine(outDir, "clips", "0003.mp4"));
                Assert.InRange(duration.TotalSeconds, 0.6, 1.0); // mp4 は等速のまま。2 倍速にするのは HTML のプレーヤー
                Assert.Contains("<option value=\"2\" selected>", File.ReadAllText(Path.Combine(outDir, Export.HtmlFile)));
                Assert.Equal((200, 100), (w, h));
                Assert.True(File.Exists(Path.Combine(outDir, "clips", "0003.png")));
            }
            else
            {
                var gif = new GifBitmapDecoder(new Uri(Path.Combine(outDir, "clips", "0003.gif")), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Assert.Equal(4, gif.Frames.Count); // 10fps で 0.8 秒の 2 倍速
                Assert.Equal(200, gif.Frames[0].PixelWidth);
            }
        }
    }

    static BitmapSource Solid(int w, int h, Color c)
    {
        var px = new byte[w * h * 3];
        for (var i = 0; i < px.Length; i += 3) (px[i], px[i + 1], px[i + 2]) = (c.B, c.G, c.R);
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, px, w * 3);
        b.Freeze();
        return b;
    }
}
