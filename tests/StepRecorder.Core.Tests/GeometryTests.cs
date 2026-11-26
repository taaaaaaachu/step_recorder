using StepRecorder.Core;

namespace StepRecorder.Core.Tests;

public class GeometryTests
{
    [Theory]
    [InlineData(0, 250, 1000, 1250)]
    [InlineData(300, 700, 2000, 2500)]
    [InlineData(1500, 500, 4000, 3200)]
    public void タイムラインを拡大縮小してもマウスの下の時刻が動かない(double offset, double at, double oldWidth, double newWidth)
    {
        var moved = Geometry.ZoomScroll(offset, at, oldWidth, newWidth, 10);
        Assert.Equal((offset + at - 10) / (oldWidth - 20), (moved + at - 10) / (newWidth - 20), 6);
    }

    [Fact]
    public void タイムラインの端では左にはみ出さない()
    {
        Assert.Equal(0, Geometry.ZoomScroll(0, 0, 1000, 2000, 10));
        Assert.Equal(0, Geometry.ZoomScroll(100, 900, 2000, 1000, 10));
    }

    [Fact]
    public void 現在位置が見えなくなったときだけスクロールする()
    {
        Assert.Equal(100, Geometry.ScrollToShow(100, 800, 900));
        Assert.Equal(750, Geometry.ScrollToShow(100, 800, 950));
        Assert.Equal(300, Geometry.ScrollToShow(1000, 800, 500));
        Assert.Equal(0, Geometry.ScrollToShow(1000, 800, 20));
    }

    [Fact]
    public void コマ送りは隣のコマの頭へ動き両端で止まる()
    {
        Assert.Equal(16 / 15.0, Geometry.StepFrame(1.03, 1, 15, 3), 9); // コマの途中から次のコマへ
        Assert.Equal(14 / 15.0, Geometry.StepFrame(1.03, -1, 15, 3), 9); // 映っているコマ(15)の 1 つ前へ
        Assert.Equal(17 / 15.0, Geometry.StepFrame(16 / 15.0, 1, 15, 3), 9); // 割り切れない時刻でも 1 コマずつ
        Assert.Equal(0, Geometry.StepFrame(0.02, -1, 15, 3));
        Assert.Equal(3, Geometry.StepFrame(2.99, 1, 15, 3));
    }

    [Fact]
    public void 始点終点の向きに関係なく正規化し画像内に収める()
    {
        Assert.Equal(new PixelRect(10, 20, 30, 40), Geometry.FromPoints(40, 60, 10, 20, 100, 100));
        Assert.Equal(new PixelRect(0, 0, 100, 50), Geometry.FromPoints(-5, -5, 150, 50, 100, 100));
    }

    [Fact]
    public void 移動は画像の外に出ない()
    {
        var r = new PixelRect(10, 10, 20, 20);
        Assert.Equal(new PixelRect(15, 5, 20, 20), Geometry.Move(r, 5, -5, 100, 100));
        Assert.Equal(new PixelRect(80, 0, 20, 20), Geometry.Move(r, 500, -500, 100, 100));
    }

    [Fact]
    public void リサイズは最小サイズを守る()
    {
        var r = new PixelRect(10, 10, 20, 20);
        Assert.Equal(new PixelRect(10, 10, 40, 50), Geometry.ResizeBottomRight(r, 50, 60, 100, 100));
        Assert.Equal(new PixelRect(10, 10, 4, 4), Geometry.ResizeBottomRight(r, 0, 0, 100, 100));
        Assert.Equal(new PixelRect(10, 10, 90, 90), Geometry.ResizeBottomRight(r, 999, 999, 100, 100));
    }

    [Fact]
    public void 当たり判定()
    {
        var r = new PixelRect(10, 10, 20, 20);
        Assert.True(Geometry.Contains(r, 15, 29));
        Assert.False(Geometry.Contains(r, 31, 15));
        Assert.True(Geometry.NearBottomRight(r, 33, 28, 3));
        Assert.False(Geometry.NearBottomRight(r, 20, 20, 3));
        Assert.False(Geometry.IsUsable(new PixelRect(0, 0, 3, 50)));
    }
}

public class CropForTests
{
    [Fact]
    public void 操作したウィンドウの範囲を全体画像の座標で返す()
    {
        var all = new PixelRect(100, 50, 800, 600);
        Assert.Equal(new PixelRect(300, 200, 200, 100), Geometry.CropFor(all, new PixelRect(400, 250, 200, 100)));
        Assert.Null(Geometry.CropFor(all, all)); // 全体と同じならトリミングしない
        Assert.Equal(new PixelRect(0, 0, 50, 50), Geometry.CropFor(all, new PixelRect(50, 0, 100, 100))); // はみ出す分は削る
    }
}

public class CenterInTests
{
    [Fact]
    public void 収まるなら大きさはそのままで真ん中に置く() =>
        Assert.Equal(new PixelRect(-1920 + 320, 130, 1280, 820), Geometry.CenterIn(new PixelRect(-1920, 0, 1920, 1080), 1280, 820));

    [Fact]
    public void はみ出す向きは作業領域の大きさまで縮める() => // 150% の 1280x820 を、100% のサブ画面(タスクバーを除く)に出すとき
        Assert.Equal(new PixelRect(-1920, 0, 1920, 1032), Geometry.CenterIn(new PixelRect(-1920, 0, 1920, 1032), 1920, 1230));
}

public class RestoreInTests
{
    static readonly PixelRect Primary = new(0, 0, 1920, 1032);

    [Fact]
    public void 収まっていればそのまま() =>
        Assert.Equal(new PixelRect(200, 150, 900, 600), Geometry.RestoreIn(new(200, 150, 900, 600), Primary, Primary));

    [Fact]
    public void どのモニターにも重ならなければメインモニターの真ん中() =>
        Assert.Equal(new PixelRect(510, 216, 900, 600), Geometry.RestoreIn(new(4000, 0, 900, 600), null, Primary));

    [Fact]
    public void 外したモニターで覚えた大きさがメインモニターより大きければ収める() => // 4K で覚えて、4K を外した
        Assert.Equal(Primary, Geometry.RestoreIn(new(1920, 0, 3000, 2000), null, Primary));

    [Fact]
    public void 重なるモニターより大きければそのモニターに収めて真ん中() => // 4K を同じ位置の HD に替えた
        Assert.Equal(new PixelRect(1920, 0, 1920, 1032), Geometry.RestoreIn(new(1920, 0, 3000, 2000), new(1920, 0, 1920, 1032), Primary));
}
