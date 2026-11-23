using System.IO;
using System.Windows.Media.Imaging;
using StepRecorder.Core;

namespace StepRecorder.App.Tests;

/// <summary>配布用の動画へのキーとマウスの焼き込み。白い動画を作って、キーの表示(暗い角丸と白い文字)やクリックの輪が写るかを見る。入力は送らない。</summary>
public sealed class KeyBurnTests : IDisposable
{
    const int W = 640, H = 360;
    readonly string dir = Path.Combine(Path.GetTempPath(), "sr-burn-" + Guid.NewGuid());
    readonly string source;

    public KeyBurnTests() => source = White("white.mp4", W, H);

    string White(string name, int w, int h)
    {
        var path = Path.Combine(dir, name);
        var px = new byte[w * h * 4];
        Array.Fill(px, (byte)255);
        var nv12 = Nv12.FromBgra(px, w, h);
        Task.Run(() => VideoRecorder.Encode(path, w, h, Enumerable.Range(0, 60).Select(i => (nv12, TimeSpan.FromSeconds(i / 15.0))))).Wait();
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    static readonly IReadOnlyList<KeyStroke> Strokes = [new(TimeSpan.FromSeconds(1), "Ctrl+S", false)]; // 1 秒から 2.5 秒まで出る

    /// <summary>下の中央の帯(キーの表示が出る所)の暗い画素の割合と、左上の暗い画素の割合。</summary>
    static (double Bottom, double Corner) Dark(BitmapSource image)
    {
        var (w, h) = (image.PixelWidth, image.PixelHeight);
        var px = VideoE2ETests.Bgr24(image);
        double Ratio(int x0, int y0, int x1, int y1)
        {
            int dark = 0, n = 0;
            for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++, n++)
                    if (px[(y * w + x) * 3 + 1] < 80) dark++;
            return (double)dark / n;
        }
        return (Ratio(w * 2 / 5, h * 3 / 4, w * 3 / 5, h - 2), Ratio(0, 0, w / 5, h / 5));
    }

    [DesktopFact]
    public void mp4には押していた間だけキーが焼き込まれる()
    {
        var clip = Path.Combine(dir, "clip.mp4");
        VideoClips.ExportClip(dir, new Step { VideoFile = "white.mp4", VideoStart = TimeSpan.FromSeconds(0.5) }, ClipFormat.Mp4, clip, null, Strokes, []);

        // 書き出した動画の時刻は区間の始め(0.5 秒)からなので、キーは 0.5〜2 秒に出る
        Assert.True(Dark(VideoClips.Frame(clip, TimeSpan.FromSeconds(0.2))).Bottom < 0.01, "押す前に出ている");
        var (bottom, corner) = Dark(VideoClips.Frame(clip, TimeSpan.FromSeconds(1.2)));
        Assert.True(bottom > 0.1, $"焼き込まれていない: {bottom:P1}");
        Assert.True(corner < 0.01, "下の中央以外に出ている");
        Assert.True(Dark(VideoClips.Frame(clip, TimeSpan.FromSeconds(2.4))).Bottom < 0.01, "消えていない");
    }

    [DesktopFact]
    public void 出さない設定やキーが無ければ焼き込まない()
    {
        var clip = Path.Combine(dir, "plain.mp4");
        VideoClips.ExportClip(dir, new Step { VideoFile = "white.mp4" }, ClipFormat.Mp4, clip, null, [], []);
        Assert.True(Dark(VideoClips.Frame(clip, TimeSpan.FromSeconds(1.5))).Bottom < 0.01);
    }

    [DesktopFact]
    public void GIFとトリミングした動画でも_その範囲の下の中央に出る()
    {
        var gif = Path.Combine(dir, "clip.gif");
        VideoClips.ExportClip(dir, new Step { VideoFile = "white.mp4" }, ClipFormat.Gif, gif, null, Strokes, []);
        var frames = new GifBitmapDecoder(new Uri(gif), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames;
        Assert.True(Dark(frames[5]).Bottom < 0.01); // 0.5 秒(10fps)
        Assert.True(Dark(frames[15]).Bottom > 0.1); // 1.5 秒

        var cropped = Path.Combine(dir, "cropped.mp4");
        VideoClips.ExportClip(dir, new Step { VideoFile = "white.mp4", Crop = new(100, 50, 320, 180) }, ClipFormat.Mp4, cropped, null, Strokes, []);
        var frame = VideoClips.Frame(cropped, TimeSpan.FromSeconds(1.5));
        Assert.Equal((320, 180), (frame.PixelWidth, frame.PixelHeight));
        Assert.True(Dark(frame).Bottom > 0.1, "トリミングした範囲に出ていない");
    }

    /// <summary>(x, y) を中心とする一辺 2r の範囲の、赤い(クリックの輪の)画素と暗い(ホイールの丸の)画素の割合。</summary>
    static (double Red, double Dark) Around(BitmapSource image, int x, int y, int r)
    {
        var w = image.PixelWidth;
        var px = VideoE2ETests.Bgr24(image);
        int red = 0, dark = 0, n = 0;
        for (var j = y - r; j < y + r; j++)
            for (var i = x - r; i < x + r; i++, n++)
            {
                var (b, g, rr) = (px[(j * w + i) * 3], px[(j * w + i) * 3 + 1], px[(j * w + i) * 3 + 2]);
                if (rr - g > 60 && rr - b > 60) red++;
                if (rr < 80 && g < 80 && b < 80) dark++;
            }
        return ((double)red / n, (double)dark / n);
    }

    static readonly IReadOnlyList<MouseStroke> Clicks = [new(TimeSpan.FromSeconds(1), 300, 200, MouseAction.Left), new(TimeSpan.FromSeconds(1), 500, 100, MouseAction.WheelDown)];

    [DesktopFact]
    public void クリックとホイールは_その位置にトリミングした分ずらして焼き込まれる()
    {
        var clip = Path.Combine(dir, "mouse.mp4");
        VideoClips.ExportClip(dir, new Step { VideoFile = "white.mp4", Crop = new(100, 50, 520, 300) }, ClipFormat.Mp4, clip, null, [], Clicks);

        var r = (int)MouseOverlay.Radius(300);
        Assert.Equal((0.0, 0.0), Around(VideoClips.Frame(clip, TimeSpan.FromSeconds(0.9)), 200, 150, r)); // 押す前
        var frame = VideoClips.Frame(clip, TimeSpan.FromSeconds(1.3));
        Assert.True(Around(frame, 200, 150, r).Red > 0.1, $"クリックの輪が無い: {Around(frame, 200, 150, r)}");
        Assert.True(Around(frame, 400, 50, r).Dark > 0.2, $"ホイールの丸が無い: {Around(frame, 400, 50, r)}");
        Assert.Equal(0.0, Around(frame, 60, 250, r).Red); // 他の所には出ない
        Assert.Equal((0.0, 0.0), Around(VideoClips.Frame(clip, TimeSpan.FromSeconds(1.7)), 200, 150, r)); // 消えた(輪は 0.6 秒)
    }

    [DesktopFact]
    public void 縮めるGIFでは縮めたあとの位置に焼き込まれる()
    {
        White("wide.mp4", 1920, 1080);
        var gif = Path.Combine(dir, "wide.gif");
        VideoClips.ExportClip(dir, new Step { VideoFile = "wide.mp4" }, ClipFormat.Gif, gif, null, [], [new(TimeSpan.FromSeconds(1), 1500, 900, MouseAction.Right)]);
        var frame = new GifBitmapDecoder(new Uri(gif), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[12]; // 1.2 秒(10fps)
        Assert.Equal(960, frame.PixelWidth);
        var r = (int)MouseOverlay.Radius(540);
        var blue = Bgr(frame).Where(p => p.B - p.R > 60).ToList();
        Assert.NotEmpty(blue);
        Assert.InRange(blue.Average(p => p.X), 750 - 3, 750 + 3); // 1500 / 2
        Assert.InRange(blue.Average(p => p.Y), 450 - 3, 450 + 3);
        Assert.True(blue.Max(p => Math.Abs(p.X - 750)) <= r + 2, "大きさが縮めたあとの高さに合っていない");
    }

    static IEnumerable<(int X, int Y, int R, int G, int B)> Bgr(BitmapSource image)
    {
        var px = VideoE2ETests.Bgr24(image);
        for (var i = 0; i < image.PixelWidth * image.PixelHeight; i++)
            yield return (i % image.PixelWidth, i / image.PixelWidth, px[i * 3 + 2], px[i * 3 + 1], px[i * 3]);
    }

    [DesktopFact]
    public void 焼き込みは表示が変わったときだけ描くので速い()
    {
        var many = Enumerable.Range(0, 40).Select(i => new KeyStroke(TimeSpan.FromSeconds(i * 0.1), ((char)('a' + i % 26)).ToString(), true)).ToList();
        var burner = new KeyBurner(KeyOverlay.Chips(many));
        var bgra = new byte[1920 * 1080 * 4];
        burner.Apply(bgra, 1920, 1080, TimeSpan.FromSeconds(1)); // 最初の 1 回は描く
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 30; i++) burner.Apply(bgra, 1920, 1080, TimeSpan.FromSeconds(5)); // 同じ表示が続く
        Assert.True(clock.ElapsedMilliseconds / 30.0 < 5, $"1 枚 {clock.ElapsedMilliseconds / 30.0:0.0}ms");
    }
}
