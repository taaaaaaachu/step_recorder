namespace StepRecorder.Core.Tests;

public class FrameRateTests
{
    [Fact]
    public void 選べるのは録画したとき以下で_録画したときの値は必ず入る()
    {
        Assert.Equal([5, 10, 15, 24, 30], FrameRate.UpTo(30));
        Assert.Equal([5, 10, 15], FrameRate.UpTo(15));
        Assert.Equal([5, 10, 12], FrameRate.UpTo(12)); // 選択肢に無い値で録った動画でも、その値を選べる
    }

    [Theory]
    [InlineData(null, 30, 30)]
    [InlineData(10, 30, 10)]
    [InlineData(60, 30, 30)] // 録画したときより上げられない
    [InlineData(0, 30, 30)] // 手で壊された値
    public void ステップで使うフレームレート(int? chosen, int recorded, int used) =>
        Assert.Equal(used, new Step { VideoFps = chosen }.FpsFor(recorded));

    [Fact]
    public void 設定の既定は15で_選べない値は既定にする()
    {
        Assert.Equal(15, new Settings().Fps);
        Assert.Equal(24, FrameRate.Valid(24));
        Assert.Equal(15, FrameRate.Valid(7));
        Assert.Equal("30fps", FrameRate.Label(30));
    }
}
