namespace StepRecorder.ColorDiag;

/// <summary>検査に使う縦縞。グレーの各段階と赤・緑・青。縦縞なので上下の並び順に左右されない。</summary>
static class Chart
{
    static readonly (byte R, byte G, byte B)[] Bars =
        [.. Judge.Levels.Select(v => ((byte)v, (byte)v, (byte)v)), (255, 0, 0), (0, 255, 0), (0, 0, 255)];

    public static byte[] Bgra(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (r, g, b) = Bars[x * Bars.Length / w];
                var i = (y * w + x) * 4;
                (px[i], px[i + 1], px[i + 2], px[i + 3]) = (b, g, r, 255);
            }
        return px;
    }

    static int Center(int bar, int w) => (int)((bar + 0.5) * w / Bars.Length);

    /// <summary>NV12 から、グレー各段階の Y と、赤・緑・青の YUV。</summary>
    public static Reading FromNv12(byte[] data, int w, int h, int stride)
    {
        var total = data.Length / stride * 2 / 3; // デコーダーは高さを 16 の倍数などに切り上げる
        var row = h / 2 & ~1;
        var colors = new List<string>();
        for (var i = Judge.Levels.Length; i < Bars.Length; i++)
        {
            var x = Center(i, w) & ~1;
            var uv = stride * total + row / 2 * stride + x;
            colors.Add($"({data[row * stride + x]},{data[uv]},{data[uv + 1]})");
        }
        return new([.. Judge.Levels.Select((_, i) => (int)data[row * stride + Center(i, w)])], string.Join(" ", colors), Yuv: true);
    }

    /// <summary>BGRA から、グレー各段階の値と、赤・緑・青の RGB。</summary>
    public static Reading FromBgra(byte[] data, int w, int h, int stride)
    {
        var row = h / 2 * stride;
        var colors = new List<string>();
        for (var i = Judge.Levels.Length; i < Bars.Length; i++)
        {
            var p = row + Center(i, w) * 4;
            colors.Add($"({data[p + 2]},{data[p + 1]},{data[p]})");
        }
        return new([.. Judge.Levels.Select((_, i) => (int)data[row + Center(i, w) * 4 + 1])], string.Join(" ", colors), Yuv: false);
    }
}

/// <summary>縦縞を測った値。</summary>
sealed record Reading(int[] Gray, string Colors, bool Yuv);
