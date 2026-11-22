using StepRecorder.Core;

namespace StepRecorder.Core.Tests;

public class Nv12Tests
{
    static byte[] Solid(int w, int h, int r, int g, int b)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4) (px[i], px[i + 1], px[i + 2], px[i + 3]) = ((byte)b, (byte)g, (byte)r, 255);
        return px;
    }

    [Theory]
    [InlineData(2, 2)] // BT.601
    [InlineData(1026, 2)] // BT.709
    public void 全段階のグレーが16から235に収まり白に潰れない(int w, int h)
    {
        for (var v = 0; v < 256; v++)
        {
            var nv = Nv12.FromBgra(Solid(w, h, v, v, v), w, h);
            Assert.InRange(nv[0], 16 + v * 219 / 255.0 - 0.51, 16 + v * 219 / 255.0 + 0.51);
            Assert.Equal((128, 128), (nv[w * h], nv[w * h + 1]));
        }
        Assert.True(Nv12.FromBgra(Solid(w, h, 240, 240, 240), w, h)[0] < Nv12.FromBgra(Solid(w, h, 255, 255, 255), w, h)[0]);
    }

    [Theory]
    [InlineData(2, 255, 0, 0, 81, 90, 240)]
    [InlineData(2, 0, 255, 0, 145, 54, 34)]
    [InlineData(2, 0, 0, 255, 41, 240, 110)]
    [InlineData(1026, 255, 0, 0, 63, 102, 240)]
    [InlineData(1026, 0, 255, 0, 173, 42, 26)]
    [InlineData(1026, 0, 0, 255, 32, 240, 118)]
    public void 原色が規格どおりの値になる(int w, int r, int g, int b, int y, int u, int v)
    {
        var nv = Nv12.FromBgra(Solid(w, 2, r, g, b), w, 2);
        Assert.Equal((y, u, v), (nv[0], nv[w * 2], nv[w * 2 + 1]));
    }

    [Theory]
    [InlineData(640, 480, false)]
    [InlineData(1024, 576, false)]
    [InlineData(1026, 576, true)]
    [InlineData(1024, 578, true)]
    [InlineData(800, 1000, true)]
    [InlineData(1920, 1080, true)]
    public void 係数はWindowsと同じく大きさで選ぶ(int w, int h, bool bt709) => Assert.Equal(bt709, Nv12.IsBt709(w, h));

    [Fact]
    public void 上の行から左から並び_色差は2かける2の平均になる()
    {
        var px = Solid(4, 4, 0, 0, 0);
        (px[0], px[1], px[2]) = (255, 255, 255); // 左上
        (px[^4], px[^3], px[^2]) = (0, 0, 255); // 右下だけ赤
        var nv = Nv12.FromBgra(px, 4, 4);

        Assert.Equal(24, nv.Length);
        Assert.Equal([235, 16, 16, 16], nv[..4]);
        Assert.Equal([16, 16, 16, 81], nv[12..16]);
        Assert.Equal([128, 128, 128, 128, 128, 128], nv[16..22]);
        Assert.Equal([119, 156], nv[22..]); // 赤(90, 240)と黒 3 つ(128, 128)の平均
    }

    [Fact]
    public void 奇数の大きさは受け付けない() => Assert.Throws<ArgumentException>(() => Nv12.FromBgra(new byte[3 * 2 * 4], 3, 2));
}
