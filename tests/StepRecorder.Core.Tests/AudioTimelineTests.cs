namespace StepRecorder.Core.Tests;

public class AudioTimelineTests
{
    // 1 秒 100 サンプル、1 サンプル 1 バイトにして数えやすくする
    static readonly TimeSpan Ms100 = TimeSpan.FromSeconds(0.1);
    static byte[] Sound(int samples, byte value) => Enumerable.Repeat(value, samples).ToArray();
    static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void 届いた分はそのまま並び_届いていない所は無音になる()
    {
        var t = new AudioTimeline(100, 1);
        t.Add(Sound(10, 1), S(0.1));
        t.Add(Sound(10, 2), S(0.2));
        Assert.Equal([.. Sound(10, 1), .. Sound(10, 2), .. Sound(10, 0)], t.Take(S(0.3)));
        Assert.Empty(t.Take(S(0.3))); // 渡した分は二度渡さない
    }

    [Fact]
    public void 鳴っていない間にデータが来なければ_その間を無音で埋めてずらさない()
    {
        var t = new AudioTimeline(100, 1);
        t.Add(Sound(10, 1), S(0.1));
        t.Add(Sound(10, 2), S(1.0)); // 0.9〜1.0 秒に鳴った音
        var all = t.Take(S(1.0));
        Assert.Equal(100, all.Length);
        Assert.Equal(Sound(10, 1), all[..10]);
        Assert.Equal(Sound(80, 0), all[10..90]);
        Assert.Equal(Sound(10, 2), all[90..]);
    }

    [Fact]
    public void 届く時刻の少しの揺れでは無音を挟まない()
    {
        var t = new AudioTimeline(100, 1);
        t.Add(Sound(10, 1), S(0.1));
        t.Add(Sound(10, 2), S(0.23)); // 3 サンプル遅れて届いた
        Assert.Equal([.. Sound(10, 1), .. Sound(10, 2)], t.Take(S(0.2)));
    }

    [Fact]
    public void 先に無音で渡した所へ遅れて届いた音は_そのあとに続ける()
    {
        var t = new AudioTimeline(100, 1);
        Assert.Equal(Sound(10, 0), t.Take(Ms100));
        t.Add(Sound(10, 1), S(0.1));
        Assert.Equal(Sound(10, 1), t.Take(S(0.2)));
    }

    [Fact]
    public void 無音の印が付いたデータは中身を見ない()
    {
        var t = new AudioTimeline(100, 2);
        t.Add(Sound(20, 9), S(0.1), silent: true);
        Assert.Equal(Sound(20, 0), t.Take(S(0.1)));
    }
}
