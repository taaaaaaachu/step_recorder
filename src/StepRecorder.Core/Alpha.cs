namespace StepRecorder.Core;

/// <summary>透明度のある画像の重ね合わせ。</summary>
public static class Alpha
{
    /// <summary>
    /// src(アルファ乗算済みの BGRA)を dst(BGRA)の上に重ねる。WGC のフレームはアルファ乗算済み。
    /// 不透明な画素はそのまま写し、透明な画素(Windows 11 のウィンドウの角の外など)は dst を残し、半透明(縁の線など)は混ぜる。
    /// </summary>
    public static void Over(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        for (var i = 0; i < src.Length; i += 4)
        {
            var a = src[i + 3];
            if (a == 255) src.Slice(i, 4).CopyTo(dst[i..]);
            else if (a != 0)
                for (var c = 0; c < 3; c++) dst[i + c] = (byte)Math.Min(255, src[i + c] + dst[i + c] * (255 - a) / 255);
        }
    }
}
