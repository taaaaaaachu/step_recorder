namespace StepRecorder.Core;

/// <summary>
/// BGRA(上の行から)を、H.264 エンコーダーがそのまま受け取る NV12 にする。明るさは 16〜235、色差は 16〜240。
/// Windows に BGRA を渡して変換させると、PC によっては 219/255 に縮めずに 16 を足され、239 以上が全部真っ白になる。
/// </summary>
public static class Nv12
{
    // 係数は 16bit の固定小数(R, G, B の順)。Y は和が 219/255、U・V は和が 0
    readonly record struct Matrix(int Yr, int Yg, int Yb, int Ur, int Ug, int Ub, int Vr, int Vg, int Vb);

    static readonly Matrix Bt601 = new(16829, 33039, 6416, -9714, -19070, 28784, 28784, -24103, -4681);
    static readonly Matrix Bt709 = new(11966, 40254, 4064, -6596, -22189, 28785, 28784, -26145, -2639);

    /// <summary>
    /// BT.709 を使う大きさか。Windows の変換・再生と同じ規則。mp4 に色の情報は書かない
    /// (書くと、小さい動画からフレームを取り出したときに Windows が色を変えてしまう)ので、大きさの規則で揃える。
    /// </summary>
    public static bool IsBt709(int width, int height) => width > 1024 || height > 576;

    /// <summary>width・height は偶数。</summary>
    public static byte[] FromBgra(byte[] bgra, int width, int height)
    {
        if (width % 2 != 0 || height % 2 != 0) throw new ArgumentException("NV12 は縦横とも偶数");
        var m = IsBt709(width, height) ? Bt709 : Bt601;
        var stride = width * 4;
        var dst = new byte[width * height * 3 / 2];
        for (var y = 0; y < height; y += 2)
        {
            var top = bgra.AsSpan(y * stride, stride);
            var bottom = bgra.AsSpan((y + 1) * stride, stride);
            var lumaTop = dst.AsSpan(y * width, width);
            var lumaBottom = dst.AsSpan((y + 1) * width, width);
            var chroma = dst.AsSpan(width * height + y / 2 * width, width);
            for (var x = 0; x < width; x += 2)
            {
                var a = top.Slice(x * 4, 8);
                var b = bottom.Slice(x * 4, 8);
                lumaTop[x] = Luma(m, a[2], a[1], a[0]);
                lumaTop[x + 1] = Luma(m, a[6], a[5], a[4]);
                lumaBottom[x] = Luma(m, b[2], b[1], b[0]);
                lumaBottom[x + 1] = Luma(m, b[6], b[5], b[4]);

                // 色差は 2×2 画素の平均(和のまま計算して 18bit 右へずらす)
                int red = a[2] + a[6] + b[2] + b[6], green = a[1] + a[5] + b[1] + b[5], blue = a[0] + a[4] + b[0] + b[4];
                chroma[x] = (byte)((m.Ur * red + m.Ug * green + m.Ub * blue + (128 << 18) + (1 << 17)) >> 18);
                chroma[x + 1] = (byte)((m.Vr * red + m.Vg * green + m.Vb * blue + (128 << 18) + (1 << 17)) >> 18);
            }
        }
        return dst;
    }

    static byte Luma(in Matrix m, int r, int g, int b) => (byte)((m.Yr * r + m.Yg * g + m.Yb * b + (16 << 16) + (1 << 15)) >> 16);
}
