using System.IO;
using System.Windows.Media.Imaging;
using StepRecorder.Core;
using Vortice.MediaFoundation;

namespace StepRecorder.App.Tests;

/// <summary>実際に mp4 へエンコードして読み戻す(Media Foundation が要るので E2E のときだけ)。</summary>
public sealed class EncodeTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "sr-encode-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    [DesktopFact]
    public async Task エンコードしても薄いグレーが白に潰れず上下も正しい()
    {
        int[] levels = [0, 64, 128, 200, 230, 240, 248, 255];
        foreach (var (w, h) in new[] { (200, 100), (1018, 634), (1366, 768), (1920, 1080) }) // 4・16 の倍数でない幅も
        {
            var bgra = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var v = y < h / 4 ? (byte)30 : (byte)levels[x * levels.Length / w]; // 上の 1/4 は暗い帯
                    bgra.AsSpan((y * w + x) * 4, 3).Fill(v);
                }
            var nv12 = Nv12.FromBgra(bgra, w, h);
            var path = Path.Combine(dir, $"{w}x{h}.mp4");
            await VideoRecorder.Encode(path, w, h, Enumerable.Range(0, 15).Select(i => (nv12, TimeSpan.FromSeconds(i / 15.0))));

            var read = new byte[w * h * 4];
            VideoClips.Frame(path, TimeSpan.FromSeconds(0.4)).CopyPixels(read, w * 4, 0);
            for (var i = 0; i < levels.Length; i++)
                Assert.InRange(read[(h * 3 / 4 * w + (int)((i + 0.5) * w / levels.Length)) * 4 + 1], levels[i] - 3, levels[i] + 3);
            Assert.InRange(read[(h / 8 * w + w / 2) * 4 + 1], 27, 33);
        }
    }

    /// <summary>取り出しを Windows に任せると、PC によっては HD の動画を BT.601 で読んで純赤が 232 になる。</summary>
    [DesktopFact]
    public async Task 取り出したフレームの色がずれない()
    {
        MediaFactory.MFStartup().CheckError();
        (int R, int G, int B)[] colors = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (65, 105, 225), (255, 255, 0), (240, 240, 240)];
        // BT.601 と 709 の両方、規則の境目、16 の倍数でない大きさ
        foreach (var (w, h) in new[] { (200, 100), (642, 482), (1024, 576), (1026, 576), (1280, 600), (800, 1000), (1366, 768), (1920, 1080) })
        {
            var bgra = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var (r, g, b) = colors[x * colors.Length / w];
                    var i = (y * w + x) * 4;
                    (bgra[i], bgra[i + 1], bgra[i + 2]) = ((byte)b, (byte)g, (byte)r);
                }
            var nv12 = Nv12.FromBgra(bgra, w, h);
            var path = Path.Combine(dir, $"color-{w}x{h}.mp4");
            await VideoRecorder.Encode(path, w, h, Enumerable.Range(0, 15).Select(i => (nv12, TimeSpan.FromSeconds(i / 15.0))));

            // ブラウザーが推測で色をずらさないよう、使った係数と範囲が mp4 に書いてある
            using (var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!))
            using (var type = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0))
            {
                Assert.Equal(Nv12.IsBt709(w, h) ? 1u : 2u, type.GetUInt32(MediaTypeAttributeKeys.YuvMatrix));
                Assert.Equal(2u, type.GetUInt32(MediaTypeAttributeKeys.VideoNominalRange));
            }

            var frame = VideoClips.Frame(path, TimeSpan.FromSeconds(0.4));
            Assert.Equal((w, h), (frame.PixelWidth, frame.PixelHeight));
            var read = new byte[w * h * 4];
            frame.CopyPixels(read, w * 4, 0);
            for (var c = 0; c < colors.Length; c++)
            {
                var p = (h / 2 * w + (int)((c + 0.5) * w / colors.Length)) * 4;
                var (r, g, b) = colors[c];
                Assert.True(Math.Abs(read[p + 2] - r) <= 6 && Math.Abs(read[p + 1] - g) <= 6 && Math.Abs(read[p] - b) <= 6,
                    $"{w}x{h} の ({r},{g},{b}) が ({read[p + 2]},{read[p + 1]},{read[p]}) になった");
            }
        }
    }

    /// <summary>単発の取り出しと、続きから読む切り出しの両方で、時刻に最も近いフレーム(同じ近さなら前)が返る。</summary>
    [DesktopFact]
    public async Task 時刻に合ったフレームを取り出し_切り出しでも順序が保たれる()
    {
        const int w = 320, h = 240;
        static byte Shade(int i) => (byte)(20 + i * 5);
        var indexes = Enumerable.Range(0, 45).ToList(); // 15fps の 3 秒。何枚目かを明るさにする
        var path = Path.Combine(dir, "index.mp4");
        await VideoRecorder.Encode(path, w, h, indexes.Select(i =>
        {
            var px = new byte[w * h * 4];
            Array.Fill(px, Shade(i));
            return (Nv12.FromBgra(px, w, h), TimeSpan.FromSeconds(i / 15.0));
        }));

        int IndexAt(string video, double sec)
        {
            var px = new byte[w * h * 4];
            VideoClips.Frame(video, TimeSpan.FromSeconds(sec)).CopyPixels(px, w * 4, 0);
            return (int)Math.Round((px[(h / 2 * w + w / 2) * 4 + 1] - 20) / 5.0);
        }
        // mp4 の時刻は 1 フレーム分(B フレームの並べ替え)うしろにずれる。再生と同じく、その時刻で数える
        int Expected(double sec) => indexes.MinBy(i => (Math.Round(Math.Abs((i + 1) / 15.0 - sec), 3), i));

        foreach (var sec in new[] { 0, 0.07, 1.0, 1.37, 1.75, 2.5, 2.93, 0.5, 9 }) // 前へ戻る、終わりを過ぎる
            Assert.Equal(Expected(sec), IndexAt(path, sec));

        var cut = Path.Combine(dir, "cut.mp4");
        VideoClips.Cut(path, TimeSpan.FromSeconds(0.8), TimeSpan.FromSeconds(2.8), null, cut);
        foreach (var k in new[] { 0, 3, 8, 11, 17, 22, 26 })
            Assert.Equal(Expected(0.8 + k / 15.0), IndexAt(cut, (k + 1) / 15.0));
    }

    /// <summary>倍速・スローの GIF は、長さが速さの分だけ変わる(mp4 は等速のままで、再生する側が変える)。</summary>
    [DesktopFact]
    public async Task GIFは倍速とスローで書き出せる()
    {
        const int w = 320, h = 240;
        static byte Shade(int i) => (byte)(20 + i * 3);
        var path = Path.Combine(dir, "speed-src.mp4");
        await VideoRecorder.Encode(path, w, h, Enumerable.Range(0, 75).Select(i => // 5 秒
        {
            var px = new byte[w * h * 4];
            Array.Fill(px, Shade(i));
            return (Nv12.FromBgra(px, w, h), TimeSpan.FromSeconds(i / 15.0));
        }));
        var (start, end) = (TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4)); // 3 秒の区間

        foreach (var speed in new[] { 0.25, 0.5, 0.75, 1.5, 2, 8 })
        {
            var gif = Path.Combine(dir, $"speed-{speed}.gif");
            var reported = new List<double>();
            VideoClips.ToGif(path, start, end, null, gif, reported.Add, speed);
            Assert.All(reported, v => Assert.InRange(v, 0, 1));
            using var stream = File.OpenRead(gif);
            var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var delays = decoder.Frames.Select(f => (int)(ushort)((BitmapMetadata)f.Metadata).GetQuery("/grctlext/Delay")).ToList();
            Assert.InRange(delays.Sum() / 100.0, 3 / speed - 0.05, 3 / speed + 0.14);
            Assert.InRange(decoder.Frames.Count, 1, 31); // スローでも等速の 30 コマより増えない
        }

        // ごく短い区間を 8 倍速にして 1 コマになっても書き出せる
        var tiny = Path.Combine(dir, "tiny.gif");
        VideoClips.ToGif(path, start, start + TimeSpan.FromSeconds(0.2), null, tiny, null, 8);
        Assert.True(File.Exists(tiny));
    }
}
