using StepRecorder.Core;

namespace StepRecorder.Core.Tests;

public class TimeTextTests
{
    [Theory]
    [InlineData(0, "0:00", "0:00.0")]
    [InlineData(5.25, "0:05", "0:05.2")]
    [InlineData(3599.99, "59:59", "59:59.9")]
    [InlineData(3600, "1:00:00", "1:00:00.0")]
    [InlineData(3605, "1:00:05", "1:00:05.0")]
    [InlineData(45296.7, "12:34:56", "12:34:56.7")]
    [InlineData(90000, "25:00:00", "25:00:00.0")]
    public void 一時間を超えても時刻が一周しない(double seconds, string text, string position)
    {
        var t = TimeSpan.FromSeconds(seconds);
        Assert.Equal(text, TimeText.Seconds(t));
        Assert.Equal(position, TimeText.Position(t));
    }
}
