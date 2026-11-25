using System.Text.Json;

namespace StepRecorder.Core.Tests;

public class SessionTests
{
    [Fact]
    public void ステップが無くても全編の動画があれば残す()
    {
        Assert.False(new Session().HasContent);
        Assert.True(new Session { Videos = [new() { File = "video.mp4" }] }.HasContent);
        Assert.True(new Session { Steps = [new()] }.HasContent);
        Assert.DoesNotContain("hasContent", JsonSerializer.Serialize(new Session(), SessionStore.Options));
    }

    [Fact]
    public void バージョン1の記録は全編の動画を_Videosに移して読む()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sr-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, SessionStore.FileName), """
                {
                  "version": 1,
                  "steps": [ { "id": 1, "videoTime": "00:00:02" }, { "id": 2 } ],
                  "videoFile": "video.mp4",
                  "keys": [ { "time": "00:00:01", "text": "a", "isChar": true } ],
                  "mouse": [ { "time": "00:00:01.5", "x": 3, "y": 4, "action": "left" } ]
                }
                """);
            var s = SessionStore.Load(dir);
            var v = Assert.Single(s.Videos);
            Assert.Equal(("video.mp4", "a", 3), (v.File, v.Keys![0].Text, v.Mouse![0].X));
            Assert.Equal(["video.mp4", null], s.Steps.Select(st => st.TimelineVideo));

            // 保存し直すと新しい形式だけになる
            SessionStore.Save(s, dir);
            var json = File.ReadAllText(Path.Combine(dir, SessionStore.FileName));
            Assert.DoesNotContain("\"videoFile\"", json);
            Assert.Contains("\"version\": 2", json);
            Assert.Equal("video.mp4", Assert.Single(SessionStore.Load(dir).Videos).File);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 続けて記録する全編の動画は_使っていない名前にする()
    {
        var rec = new Recording(new Session());
        Assert.Equal("video.mp4", rec.NextFullVideoFile);
        rec.Session.Videos.Add(new() { File = "video.mp4" });
        Assert.Equal("video-2.mp4", rec.NextFullVideoFile);
        rec.Session.Videos.Add(new() { File = "video-2.mp4" });
        Assert.Equal("video-3.mp4", rec.NextFullVideoFile);
    }

    [Fact]
    public void 全編から切り出した区間は_その全編のキーとマウスを使う()
    {
        var s = new Session
        {
            Videos =
            [
                new() { File = "video.mp4", Keys = [new(TimeSpan.FromSeconds(1), "A", true)], Mouse = [new(TimeSpan.FromSeconds(1), 1, 1, MouseAction.Left)] },
                new() { File = "video-2.mp4", Keys = [new(TimeSpan.FromSeconds(1), "B", true)], Mouse = [new(TimeSpan.FromSeconds(1), 2, 2, MouseAction.Right)] },
            ],
        };
        var second = new Step { VideoFile = "video-2.mp4" };
        Assert.Equal(["B"], KeyOverlay.Recorded(s, second).Select(k => k.Text));
        Assert.Equal([2], MouseOverlay.Recorded(s, second).Select(m => m.X));
        Assert.Equal(["A"], KeyOverlay.Recorded(s, new Step { VideoFile = "video.mp4" }).Select(k => k.Text));
        // 録画ボタンの動画は、そのステップのもの
        Assert.Equal(["C"], KeyOverlay.Recorded(s, new Step { VideoFile = "videos/0003.mp4", Keys = [new(TimeSpan.FromSeconds(1), "C", true)] }).Select(k => k.Text));
    }

    [Fact]
    public void 対象の名前は読み書きでき_表題に出る()
    {
        var s = new Session { TargetName = "画面2", CreatedAt = new DateTimeOffset(2026, 11, 23, 10, 15, 0, TimeSpan.Zero) };
        Assert.Contains("\"targetName\"", JsonSerializer.Serialize(s, SessionStore.Options));
        Assert.Equal("画面2 操作記録 (2026-11-23)", Export.Title(SessionStore.Clone(s)));
        Assert.Equal("20261123-101500_画面2", Settings.SessionFolderName(s.CreatedAt, s.TargetName));
    }
}
