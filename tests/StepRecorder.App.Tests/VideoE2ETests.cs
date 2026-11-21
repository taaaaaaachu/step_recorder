using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Storage;
using static StepRecorder.App.Tests.Desktop;

namespace StepRecorder.App.Tests;

/// <summary>テスト用の WinForms アプリを実際に録画して、mp4 になるかを確かめる。</summary>
public sealed class VideoE2ETests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "sr-video-" + Guid.NewGuid());
    Process? target;

    public void Dispose()
    {
        try { target?.Kill(); } catch (InvalidOperationException) { }
        if (Environment.GetEnvironmentVariable("STEP_RECORDER_E2E_OUT") is { Length: > 0 } outDir && Directory.Exists(dir))
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(outDir, Path.GetRelativePath(dir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(file, to, true);
            }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    [E2EFact]
    public void アプリのウィンドウを録画できる()
    {
        target = Launch(SampleExe);
        var main = target.MainWindowHandle;
        var path = Path.Combine(dir, "video.mp4");

        var rec = VideoRecorder.Start(main, path, cursor: false);
        Wait(2000);
        rec.Stop();

        var (duration, w, h) = VideoInfo(path);
        Assert.InRange(duration.TotalSeconds, 1.5, 2.5);
        var frame = Native.FrameBounds(main);
        Assert.Equal((frame.Width & ~1, frame.Height & ~1), (w, h));
    }

    [E2EFact]
    public void 途中で出たダイアログも重ねて録画し_フレームを取り出せる()
    {
        target = Launch(SampleExe);
        var main = target.MainWindowHandle;
        var path = Path.Combine(dir, "video.mp4");

        var rec = VideoRecorder.Start(main, path, cursor: false);
        Wait(800);
        ClickOn(main, "okButton"); // 確認ダイアログが出る
        Wait(1500);
        var still = WindowCapture.Capture(main)!; // 同じ時点の静止画(PrintWindow で合成したもの)
        rec.Stop();

        var before = VideoClips.Frame(path, TimeSpan.FromSeconds(0.3));
        var after = VideoClips.Frame(path, TimeSpan.FromSeconds(2.1));
        if (Environment.GetEnvironmentVariable("STEP_RECORDER_E2E_OUT") is { Length: > 0 } outDir)
        {
            WindowCapture.SavePng(before, Path.Combine(outDir, "frame-before.png"));
            WindowCapture.SavePng(after, Path.Combine(outDir, "frame-after.png"));
        }

        // 録画の最後のフレームは、静止画のうちメインウィンドウの範囲とほぼ同じ(上下逆さま・ずれ・ダイアログ抜けが無い)
        var frame = Native.FrameBounds(main);
        var (ox, oy) = (frame.Left - still.Bounds.Left, frame.Top - still.Bounds.Top);
        Assert.True(MeanDiff(after, still.Image, ox, oy) < 6, $"静止画と違う: {MeanDiff(after, still.Image, ox, oy):0.0}");
        Assert.True(MeanDiff(before, still.Image, ox, oy) > MeanDiff(after, still.Image, ox, oy) + 2, "ダイアログが写っていない");
    }

    [E2EFact]
    public void 区間をmp4とGIFに切り出せる()
    {
        target = Launch(SampleExe);
        var path = Path.Combine(dir, "video.mp4");
        var rec = VideoRecorder.Start(target.MainWindowHandle, path, cursor: false);
        Wait(3000);
        rec.Stop();

        var mp4 = Path.Combine(dir, "clips", "cut.mp4");
        VideoClips.Cut(path, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2.5), mp4);
        Assert.InRange(VideoInfo(mp4).Duration.TotalSeconds, 1.3, 1.7);

        var gif = Path.Combine(dir, "clips", "cut.gif");
        VideoClips.ToGif(path, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), gif);
        var decoder = new GifBitmapDecoder(new Uri(gif), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.Equal(10, decoder.Frames.Count); // 10fps で1秒
        Assert.Equal((ushort)10, ((BitmapMetadata)decoder.Frames[0].Metadata).GetQuery("/grctlext/Delay"));
        Assert.Contains("NETSCAPE2.0", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(gif))); // 繰り返し再生
    }

    /// <summary>a の各画素と、b の (ox, oy) からの同じ大きさの範囲との、色の差の平均。</summary>
    static double MeanDiff(BitmapSource a, BitmapSource b, int ox, int oy)
    {
        byte[] Bgr(BitmapSource s)
        {
            var c = new FormatConvertedBitmap(s, PixelFormats.Bgr24, null, 0);
            var buf = new byte[c.PixelWidth * c.PixelHeight * 3];
            c.CopyPixels(buf, c.PixelWidth * 3, 0);
            return buf;
        }
        var (pa, pb) = (Bgr(a), Bgr(b));
        long sum = 0, n = 0;
        for (var y = 0; y < a.PixelHeight && y + oy < b.PixelHeight; y++)
            for (var x = 0; x < a.PixelWidth * 3 && x + ox * 3 < b.PixelWidth * 3; x++, n++)
                sum += Math.Abs(pa[y * a.PixelWidth * 3 + x] - pb[(y + oy) * b.PixelWidth * 3 + ox * 3 + x]);
        return (double)sum / n;
    }

    internal static (TimeSpan Duration, int Width, int Height) VideoInfo(string path)
    {
        var file = StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask().GetAwaiter().GetResult();
        var p = file.Properties.GetVideoPropertiesAsync().AsTask().GetAwaiter().GetResult();
        return (p.Duration, (int)p.Width, (int)p.Height);
    }
}
