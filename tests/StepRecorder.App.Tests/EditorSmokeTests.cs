using System.Diagnostics;
using System.IO;
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
