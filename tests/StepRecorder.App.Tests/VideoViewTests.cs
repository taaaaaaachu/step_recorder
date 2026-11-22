using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StepRecorder.Core;

namespace StepRecorder.App.Tests;

/// <summary>編集画面の動画の表示(VideoView)。実際に復号するので E2E のときだけ。</summary>
public sealed class VideoViewTests : IDisposable
{
    const int W = 1280, H = 720, Frames = 30;
    readonly string dir = Path.Combine(Path.GetTempPath(), "sr-view-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    /// <summary>左半分は純赤、右半分は何枚目かを明るさにしたグレー。</summary>
    async Task<string> MakeVideo()
    {
        var path = Path.Combine(dir, "view.mp4");
        await VideoRecorder.Encode(path, W, H, Enumerable.Range(0, Frames).Select(i =>
        {
            var px = new byte[W * H * 4];
            for (var y = 0; y < H; y++)
            {
                for (var x = 0; x < W / 2; x++) px[(y * W + x) * 4 + 2] = 255;
                px.AsSpan((y * W + W / 2) * 4, W / 2 * 4).Fill((byte)(240 - i * 6));
            }
            return (Nv12.FromBgra(px, W, H), TimeSpan.FromSeconds(i / 15.0));
        }));
        return path;
    }

    [DesktopFact]
    public async Task シークと再生で正しい色のフレームを出し_閉じるとファイルを離す()
    {
        var path = await MakeVideo();
        OnSta(() =>
        {
            var view = new VideoView();
            var ended = 0;
            view.Ended += () => ended++;

            view.Open(path, TimeSpan.Zero);
            Assert.True(PumpUntil(() => view.Source != null), "最初のフレームが出ない");
            var (red, gray) = Pixels(view);
            Assert.True(red.R >= 251 && red.G <= 4 && red.B <= 4, $"純赤が {red}");
            Assert.InRange(gray, 237, 243); // 眠い色なら 222 になる

            view.Position = TimeSpan.FromSeconds(1); // 15 枚目の直前(mp4 の時刻は 1 枚分うしろ)
            Assert.True(PumpUntil(() => Math.Abs(Pixels(view).Gray - (240 - 14 * 6)) <= 3), "シークしたフレームが出ない");
            view.Position = TimeSpan.FromSeconds(0.4); // 戻る
            Assert.True(PumpUntil(() => Math.Abs(Pixels(view).Gray - (240 - 5 * 6)) <= 3), "戻ったフレームが出ない");

            view.Play();
            PumpUntil(() => false, 500);
            view.Pause();
            var paused = view.Position;
            Assert.InRange(paused.TotalSeconds, 0.7, 1.3);
            PumpUntil(() => false, 300);
            Assert.Equal(paused, view.Position); // 止まっている

            view.Play();
            Assert.True(PumpUntil(() => ended > 0, 4000), "最後まで再生しても終わらない");
            Assert.Equal(1, ended);
            Assert.True(PumpUntil(() => Math.Abs(Pixels(view).Gray - (240 - (Frames - 1) * 6)) <= 3), "最後のフレームが出ない");

            view.Close();
            Assert.Null(view.Source);
            Assert.True(PumpUntil(() => TryDelete(path), 3000), "閉じてもファイルを離さない");
        });
    }

    [DesktopFact]
    public async Task 速さを変えて再生し_区間の終わりを越えない()
    {
        var path = await MakeVideo();
        OnSta(() =>
        {
            var view = new VideoView();
            var ended = 0;
            view.Ended += () => ended++;

            view.Open(path, TimeSpan.Zero, speed: 0.25);
            Assert.True(PumpUntil(() => view.Source != null), "最初のフレームが出ない");
            view.Play();
            PumpUntil(() => false, 1200);
            Assert.InRange(view.Position.TotalSeconds, 0.2, 0.4); // 1.2 秒で 0.3 秒分

            var end = TimeSpan.FromSeconds(1.2);
            view.Open(path, TimeSpan.FromSeconds(0.2), end, 8); // 0.125 秒で終わる
            view.Play();
            Assert.True(PumpUntil(() => ended > 0, 3000), "区間の終わりで終わらない");
            Assert.Equal(end, view.Position);
            Assert.True(PumpUntil(() => Math.Abs(Pixels(view).Gray - (240 - 17 * 6)) <= 3), "区間の最後のフレームで止まっていない");
            view.Close();
        });
    }

    [DesktopFact]
    public void 開けない動画は落ちずに知らせる() => OnSta(() =>
    {
        var view = new VideoView();
        string? error = null;
        view.Failed += message => error = message;
        view.Open(Path.Combine(dir, "none.mp4"), TimeSpan.Zero);
        Assert.True(PumpUntil(() => error != null), "失敗が届かない");
    });

    static ((int R, int G, int B) Red, int Gray) Pixels(VideoView view)
    {
        var px = new byte[4];
        var src = (BitmapSource)view.Source;
        src.CopyPixels(new Int32Rect(W / 4, H / 2, 1, 1), px, 4, 0);
        var red = ((int)px[2], (int)px[1], (int)px[0]);
        src.CopyPixels(new Int32Rect(W * 3 / 4, H / 2, 1, 1), px, 4, 0);
        return (red, px[1]);
    }

    static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    static bool PumpUntil(Func<bool> cond, int timeoutMs = 5000)
    {
        var end = DateTime.Now.AddMilliseconds(timeoutMs);
        while (!cond())
        {
            if (DateTime.Now > end) return false;
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        return true;
    }

    static void OnSta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { error = e; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
    }
}
