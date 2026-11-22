using System.Text.Json;

namespace StepRecorder.Core.Tests;

public class SessionTests
{
    [Fact]
    public void ステップが無くても全編の動画があれば残す()
    {
        Assert.False(new Session().HasContent);
        Assert.True(new Session { VideoFile = "video.mp4" }.HasContent);
        Assert.True(new Session { Steps = [new()] }.HasContent);
        Assert.DoesNotContain("hasContent", JsonSerializer.Serialize(new Session(), SessionStore.Options));
    }
}
