namespace StepRecorder.Core;

/// <summary>
/// BGRA(上の行から)と NV12 の変換。明るさは 16〜235、色差は 16〜240。どちら向きも Windows には任せない。
/// BGRA を渡して変換させると、PC によっては 219/255 に縮めずに 16 を足され、239 以上が全部真っ白になる。
/// RGB で受け取ると、PC によっては HD の動画を BT.601 で読まれて色がずれる(純赤が 232 になる)。
/// </summary>
public static class Nv12
{
    // 係数は 16bit の固定小数(R, G, B の順)。Y は和が 219/255、U・V は和が 0
    readonly record struct Matrix(int Yr, int Yg, int Yb, int Ur, int Ug, int Ub, int Vr, int Vg, int Vb);

    static readonly Matrix Bt601 = new(16829, 33039, 6416, -9714, -19070, 28784, 28784, -24103, -4681);
    static readonly Matrix Bt709 = new(11966, 40254, 4064, -6596, -22189, 28785, 28784, -26145, -2639);

    // 逆向き。R = Y + Rv·V、G = Y - Gu·U - Gv·V、B = Y + Bu·U(Y は 255/219 倍してから)
    readonly record struct Inverse(int Rv, int Gu, int Gv, int Bu);

    const int LumaScale = 76309;
    static readonly Inverse FromBt601 = new(104597, 25675, 53279, 132201);
    static readonly Inverse FromBt709 = new(117489, 13975, 34925, 138438);

    /// <summary>
    /// BT.709 を使う大きさか。Windows と同じ規則。書くときも読むときもこれで揃え、mp4 の色の情報にも書く。
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

    /// <summary>
    /// 左上の width×height を、不透明な BGRA にする。stride は 1 行のバイト数、rows は明るさの面の行数
    /// (デコーダーは縦横を 16 の倍数などに切り上げて渡すので、映像の大きさと違う)。
    /// 再生では毎フレームになるので、書き先 buffer を使い回せる。
    /// </summary>
    public static byte[] ToBgra(byte[] nv12, int stride, int rows, int width, int height, byte[]? buffer = null)
    {
        var m = IsBt709(width, height) ? FromBt709 : FromBt601;
        var dst = buffer ?? new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var luma = nv12.AsSpan(y * stride, width);
            var chroma = nv12.AsSpan(stride * rows + y / 2 * stride, (width + 1) & ~1);
            var row = dst.AsSpan(y * width * 4, width * 4);
            // ponytail: 色差は 2×2 に同じ値を使う(補間しない)。画面の録画では、切り出しで作り直しても滲みが増えないこの方が良い
            for (var x = 0; x < width; x += 2)
            {
                int u = chroma[x] - 128, v = chroma[x + 1] - 128;
                int blue = m.Bu * u, green = -m.Gu * u - m.Gv * v, red = m.Rv * v;
                for (var i = x; i < Math.Min(x + 2, width); i++)
                {
                    var c = (luma[i] - 16) * LumaScale + (1 << 15);
                    var px = row.Slice(i * 4, 4);
                    px[3] = 255;
                    px[2] = Clamp((c + red) >> 16);
                    px[1] = Clamp((c + green) >> 16);
                    px[0] = Clamp((c + blue) >> 16);
                }
            }
        }
        return dst;
    }

    static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);

    static byte Luma(in Matrix m, int r, int g, int b) => (byte)((m.Yr * r + m.Yg * g + m.Yb * b + (16 << 16) + (1 << 15)) >> 16);
}
