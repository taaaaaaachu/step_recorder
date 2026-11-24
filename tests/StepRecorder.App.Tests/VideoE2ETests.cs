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
        VideoClips.Cut(path, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2.5), new(0, 0, 200, 100), mp4);
        var (duration, w, h) = VideoInfo(mp4);
        Assert.InRange(duration.TotalSeconds, 1.3, 1.7);
        Assert.Equal((200, 100), (w, h)); // トリミングした範囲だけになる

        var gif = Path.Combine(dir, "clips", "cut.gif");
        VideoClips.ToGif(path, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), null, gif);
        var decoder = new GifBitmapDecoder(new Uri(gif), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.Equal(10, decoder.Frames.Count); // 10fps で1秒
        Assert.Equal((ushort)10, ((BitmapMetadata)decoder.Frames[0].Metadata).GetQuery("/grctlext/Delay"));
        Assert.Contains("NETSCAPE2.0", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(gif))); // 繰り返し再生
    }

    [DesktopFact]
    public void エンコードに失敗しても止められ_理由が分かる()
    {
        target = Open(SampleExe);
        var path = Path.Combine(dir, "video.mp4");
        Directory.CreateDirectory(path); // 同名のフォルダがあるので書けない

        var rec = VideoRecorder.Start(target.MainWindowHandle, path, cursor: false);
        Wait(3000); // フレームの待ち行列(2 秒分)が一杯になる
        var stop = Task.Run(() => Record.Exception(rec.Stop));
        Assert.True(stop.Wait(5000), "停止が終わらない");
        Assert.NotNull(stop.Result);
    }

    [DesktopFact]
    public void 録り始めた直後に止めても_何度でも1枚は書ける()
    {
        using var window = new ColorWindow(Colors.Blue);
        for (var i = 0; i < 25; i++) // 停止が最初の 1 枚より先に届くかは間合いによる
        {
            var path = Path.Combine(dir, $"short-{i}.mp4");
            VideoRecorder.Start(window.Handle, path, cursor: false, () => true).Stop();
            Assert.True(new FileInfo(path).Length > 0);
        }
    }

    [DesktopFact]
    public void 指定したフレームレートで録画できる()
    {
        using var window = new ColorWindow(Colors.Blue);
        foreach (var fps in new[] { 5, 30 })
        {
            var path = Path.Combine(dir, $"fps-{fps}.mp4");
            var rec = VideoRecorder.Start(window.Handle, path, cursor: false, fps: fps);
            Wait(1500);
            rec.Stop();
            using var reader = VideoClips.Reader.Open(path);
            Assert.Equal(fps, reader.Fps);
            Assert.InRange(reader.Duration.TotalSeconds, 1.2, 2.0);
        }
    }

    [DesktopFact]
    public void 一時停止中は録らず_停止していた時間は詰める()
    {
        using var window = new ColorWindow(Colors.Blue);
        var path = Path.Combine(dir, "video.mp4");
        var paused = false;

        var rec = VideoRecorder.Start(window.Handle, path, cursor: false, () => Volatile.Read(ref paused));
        Wait(1000);
        Volatile.Write(ref paused, true);
        Wait(200);
        window.Paint(Colors.Red); // 停止中だけ見せる
        Wait(1500);
        var at = rec.CurrentTime;
        Wait(300);
        Assert.Equal(at, rec.CurrentTime); // 目盛りに使う時刻も止まる
        window.Paint(Colors.Lime);
        Wait(200);
        Volatile.Write(ref paused, false);
        Wait(1000);
        rec.Stop();

        Assert.InRange(VideoInfo(path).Duration.TotalSeconds, 1.6, 2.6); // 2.2 秒の停止は入らない
        // 音(このプロセスの音。鳴らしていないので無音)も入り、止めていた分は詰まって映像と同じ長さ
        Assert.Null(rec.AudioFailure);
        Assert.True(VideoClips.HasAudio(path));
        var samples = Task.Run(() => VideoClips.Reader.Audio(path, TimeSpan.Zero, TimeSpan.FromSeconds(10)).Sum(c => c.Pcm.Length) / AudioRecorder.BlockAlign).GetAwaiter().GetResult();
        Assert.InRange(samples / (double)AudioRecorder.Rate, VideoInfo(path).Duration.TotalSeconds - 0.3, VideoInfo(path).Duration.TotalSeconds + 0.3);
        var colors = new List<Color>();
        using var reader = VideoClips.Reader.Open(path);
        for (var t = TimeSpan.Zero; t < reader.Duration; t += TimeSpan.FromSeconds(1.0 / reader.Fps))
            colors.Add(Center(reader.Frame(t)));
        Assert.DoesNotContain(colors, c => c.R > 128); // 赤は 1 枚も無い
        Assert.Contains(colors, c => c.B > 128);
        Assert.Contains(colors, c => c.G > 128);
    }

    /// <summary>真ん中の画素の色。</summary>
    internal static Color Center(BitmapSource s) => ColorAt(s, s.PixelWidth / 2, s.PixelHeight / 2);

    internal static Color ColorAt(BitmapSource s, int x, int y)
    {
        var px = new byte[3];
        new FormatConvertedBitmap(s, PixelFormats.Bgr24, null, 0).CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), px, 3, 0);
        return Color.FromRgb(px[2], px[1], px[0]);
    }

    /// <summary>a の各画素と、b の (ox, oy) からの同じ大きさの範囲との、色の差の平均。</summary>
    static double MeanDiff(BitmapSource a, BitmapSource b, int ox, int oy)
    {
        var (pa, pb) = (Bgr24(a), Bgr24(b));
        long sum = 0, n = 0;
        for (var y = 0; y < a.PixelHeight && y + oy < b.PixelHeight; y++)
            for (var x = 0; x < a.PixelWidth * 3 && x + ox * 3 < b.PixelWidth * 3; x++, n++)
                sum += Math.Abs(pa[y * a.PixelWidth * 3 + x] - pb[(y + oy) * b.PixelWidth * 3 + ox * 3 + x]);
        return (double)sum / n;
    }


    /// <summary>画素の並び(1画素 3 バイト、BGR)。</summary>
    internal static byte[] Bgr24(BitmapSource s)
    {
        var c = new FormatConvertedBitmap(s, PixelFormats.Bgr24, null, 0);
        var buf = new byte[c.PixelWidth * c.PixelHeight * 3];
        c.CopyPixels(buf, c.PixelWidth * 3, 0);
        return buf;
    }

    internal static (TimeSpan Duration, int Width, int Height) VideoInfo(string path)
    {
        var file = StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask().GetAwaiter().GetResult();
        var p = file.Properties.GetVideoPropertiesAsync().AsTask().GetAwaiter().GetResult();
        return (p.Duration, (int)p.Width, (int)p.Height);
    }
}
