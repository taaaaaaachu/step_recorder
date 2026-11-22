namespace StepRecorder.Core.Tests;

public class DisplayMonitorTests
{
    [Fact]
    public void 左から右_上から下の順に番号を振る()
    {
        var monitors = DisplayMonitor.Numbered(
        [
            (10, new PixelRect(0, 0, 1920, 1080), true),
            (20, new PixelRect(1920, 0, 3840, 2160), false),
            (30, new PixelRect(0, -1440, 2560, 1440), false),
            (40, new PixelRect(-1080, 0, 1080, 1920), false),
        ]);

        Assert.Equal<nint>([40, 30, 10, 20], monitors.Select(m => m.Handle));
        Assert.Equal([1, 2, 3, 4], monitors.Select(m => m.Number));
    }

    [Fact]
    public void 一覧と表題に出す名前()
    {
        var main = new DisplayMonitor(1, 2, new(0, 0, 1920, 1080), true);

        Assert.Equal("画面 2  1920×1080(メイン)", main.Label);
        Assert.Equal("画面2", main.Name);
        Assert.Equal("画面 3  3840×2160", new DisplayMonitor(2, 3, new(1920, 0, 3840, 2160), false).Label);
    }

    static readonly PixelRect Monitor = new(-1920, 0, 1920, 1080); // メインの左にある

    [Fact]
    public void モニターの端をまたぐウィンドウは_モニターに載っている範囲で切る() =>
        Assert.Equal(new PixelRect(1620, 100, 300, 500), Geometry.CropFor(Monitor, new(-300, 100, 800, 500)));

    [Fact]
    public void モニターの中のウィンドウはモニター内の座標で切る() =>
        Assert.Equal(new PixelRect(20, 30, 400, 300), Geometry.CropFor(Monitor, new(-1900, 30, 400, 300)));

    [Theory]
    [InlineData(100, 100, 800, 500)] // 別のモニターにある
    [InlineData(0, 0, 0, 0)] // ウィンドウの範囲が取れない
    [InlineData(-1920, 0, 5760, 1080)] // 全モニターに広がるデスクトップ
    public void モニターの外や全体を覆うウィンドウでは切らない(int x, int y, int w, int h) =>
        Assert.Null(Geometry.CropFor(Monitor, new(x, y, w, h)));
}
