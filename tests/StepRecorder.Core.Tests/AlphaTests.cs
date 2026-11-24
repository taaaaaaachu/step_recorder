namespace StepRecorder.Core.Tests;

public class AlphaTests
{
    [Fact]
    public void 不透明は写し_透明は下を残し_半透明は混ぜる()
    {
        byte[] src = [10, 20, 30, 255, /* 透明 */ 0, 0, 0, 0, /* 半分の黒(乗算済み) */ 0, 0, 0, 128, /* 半分の白(乗算済み) */ 128, 128, 128, 128];
        var dst = Enumerable.Repeat((byte)200, 16).ToArray();
        Alpha.Over(src, dst);
        Assert.Equal([10, 20, 30, 255], dst[0..4]);
        Assert.Equal([200, 200, 200, 200], dst[4..8]); // 角の外は黒くならない
        Assert.Equal([99, 99, 99], dst[8..11]);
        Assert.Equal([227, 227, 227], dst[12..15]);
    }
}
