using StepRecorder.Core;

namespace StepRecorder.Core.Tests;

public class GeometryTests
{
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
