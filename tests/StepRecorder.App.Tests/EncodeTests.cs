using System.IO;
using StepRecorder.Core;

namespace StepRecorder.App.Tests;

/// <summary>実際に mp4 へエンコードして読み戻す(Media Foundation が要るので E2E のときだけ)。</summary>
public sealed class EncodeTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "sr-encode-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    [E2EFact]
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
}
