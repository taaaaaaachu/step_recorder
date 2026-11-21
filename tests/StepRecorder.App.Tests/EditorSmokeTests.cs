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

    public void Dispose()
    {
        try { app?.Kill(); app?.WaitForExit(5000); } catch (InvalidOperationException) { }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    /// <summary>記録フォルダを渡して起動すると、開始画面を出さずに編集画面が開く。</summary>
    [E2EFact]
    public void 記録フォルダを渡すと編集画面が開く()
    {
        var rec = new Recording(new Session { ProcessName = "Demo", CreatedAt = DateTimeOffset.Now });
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
    }

    [E2EFact]
    public void 編集画面はズームできる()
    {
        var rec = new Recording(new Session { ProcessName = "Demo", CreatedAt = DateTimeOffset.Now });
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
        var rec = new Recording(new Session { ProcessName = "Demo", CreatedAt = DateTimeOffset.Now });
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



    static BitmapSource Solid(int w, int h, Color c)
    {
        var px = new byte[w * h * 3];
        for (var i = 0; i < px.Length; i += 3) (px[i], px[i + 1], px[i + 2]) = (c.B, c.G, c.R);
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, px, w * 3);
        b.Freeze();
        return b;
    }
}
