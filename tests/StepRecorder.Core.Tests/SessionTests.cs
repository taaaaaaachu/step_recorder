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

    [Fact]
    public void 対象の名前は読み書きでき_表題に出る()
    {
        var s = new Session { TargetName = "画面2", CreatedAt = new DateTimeOffset(2026, 11, 23, 10, 15, 0, TimeSpan.Zero) };
        Assert.Contains("\"targetName\"", JsonSerializer.Serialize(s, SessionStore.Options));
        Assert.Equal("画面2 画面一覧 (2026-11-23)", Export.Title(SessionStore.Clone(s)));
        Assert.Equal("20261123-101500_画面2", Settings.SessionFolderName(s.CreatedAt, s.TargetName));
    }
}
