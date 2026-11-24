using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Core;
using MediaPlayer = Windows.Media.Playback.MediaPlayer;

namespace StepRecorder.App;

/// <summary>
/// 動画(音声なし)の表示と再生。書き出しと同じ方法で復号した画像を出す。
/// MediaElement は明るさの範囲をドライバーに伝えないので、NVIDIA の既定の設定では 16〜235 のまま出て眠い色になる。
/// 音は同じファイルを MediaPlayer で鳴らし、再生・一時停止・シーク・速さを映像の時計に合わせる。
/// ponytail: 音は映像と別の時計で進む。長く再生してずれが気になるなら、一定の間隔で位置を合わせ直す
/// </summary>
sealed class VideoView : Image
{
    /// <summary>開いている 1 本分。復号は専用のスレッドで、いちばん新しく頼まれた時刻のものだけを作る。</summary>
    sealed class Playback(string path, TimeSpan start)
    {
        public readonly string Path = path;
        public readonly AutoResetEvent Wake = new(true);
        public long Target = start.Ticks;
        public volatile bool Closed;
    }

    readonly DispatcherTimer timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
    readonly Stopwatch clock = new();
    Playback? playback;
    WriteableBitmap? bitmap;
    TimeSpan origin;
    TimeSpan? duration; // 再生を終える時刻(区間の終わりか、動画の終わり)。最初のフレームが出るまで null
    TimeSpan? stopAt;
    double speed = 1;
    bool playing, muted;
    MediaPlayer? sound;

    /// <summary>音を消す。</summary>
    public bool Muted
    {
        get => muted;
        set
        {
            muted = value;
            if (sound != null) sound.IsMuted = value;
        }
    }

    /// <summary>最後(区間の終わりを指定していればそこ)まで再生した。</summary>
    public event Action? Ended;

    /// <summary>開けなかった・復号できなかった。</summary>
    public event Action<string>? Failed;

    public VideoView()
    {
        Stretch = Stretch.Fill;
        timer.Tick += (_, _) => Tick();
    }

    public TimeSpan Position
    {
        get
        {
            var at = origin + clock.Elapsed * speed;
            return duration is { } end && at > end ? end : at;
        }
        set
        {
            origin = value;
            if (clock.IsRunning) clock.Restart();
            else clock.Reset();
            if (clock.IsRunning && sound != null) sound.PlaybackSession.Position = value;
            Request(value);
        }
    }

    /// <summary>
    /// at のフレームを出して止まった状態で開く。開き終える前に再生・シークしてよい。
    /// end を渡すとそこで再生を終える。speed は再生の速さ。
    /// </summary>
    public void Open(string path, TimeSpan at, TimeSpan? end = null, double speed = 1)
    {
        Close();
        origin = at;
        stopAt = end;
        this.speed = speed;
        var opened = playback = new Playback(path, at);
        new Thread(() => DecodeLoop(opened)) { IsBackground = true, Name = "video-view" }.Start();
        sound = new MediaPlayer { AutoPlay = false, IsMuted = muted, Source = MediaSource.CreateFromUri(new Uri(path)) };
        sound.CommandManager.IsEnabled = false; // Windows のメディアの操作(音量を変えたときに出る表示など)に出さない
    }

    public void Play()
    {
        if (playback == null) return;
        playing = true;
        if (duration != null) StartClock(); // 開いている途中なら、最初のフレームが出てから進める
        timer.Start();
    }

    public void Pause()
    {
        if (!playing) return;
        playing = false;
        timer.Stop();
        sound?.Pause();
        Position = Position; // 時計を止めた所のフレームにする
        clock.Reset();
    }

    public void Close()
    {
        if (playback is { } p)
        {
            p.Closed = true;
            p.Wake.Set();
        }
        playback = null;
        sound?.Dispose();
        sound = null;
        playing = false;
        timer.Stop();
        clock.Reset();
        origin = TimeSpan.Zero;
        duration = stopAt = null;
        speed = 1;
        bitmap = null;
        Source = null;
    }

    void Request(TimeSpan at)
    {
        if (playback is not { } p) return;
        Interlocked.Exchange(ref p.Target, at.Ticks);
        p.Wake.Set();
    }

    void Tick()
    {
        if (duration is not { } end) return;
        var at = Position;
        Request(at);
        if (at < end) return;
        Pause();
        Ended?.Invoke();
    }

    void DecodeLoop(Playback p)
    {
        try
        {
            using var reader = VideoClips.Reader.Open(p.Path);
            var bgra = new byte[reader.Width * reader.Height * 4];
            while (true)
            {
                p.Wake.WaitOne();
                if (p.Closed) return;
                if (!reader.DecodeIfChanged(Interlocked.Read(ref p.Target), bgra)) continue;
                // 画面へ写し終えるまで待つ(同じバッファに次を復号するため)
                Dispatcher.Invoke(() => Show(p, reader.Width, reader.Height, reader.Duration, bgra), DispatcherPriority.Render);
            }
        }
        catch (Exception) when (p.Closed || Dispatcher.HasShutdownStarted) { } // 閉じている途中
        catch (Exception e)
        {
            // 壊れた動画・消された動画。どの例外でもアプリは落とさずに知らせる
            Dispatcher.BeginInvoke(() =>
            {
                if (playback != p) return;
                Close();
                Failed?.Invoke(e.Message);
            });
        }
    }

    void Show(Playback p, int width, int height, TimeSpan length, byte[] bgra)
    {
        if (playback != p) return; // もう別の動画を開いている
        if (bitmap == null) Source = bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
        if (duration != null) return;
        duration = stopAt is { } end && end < length ? end : length;
        if (playing) StartClock();
    }

    void StartClock()
    {
        clock.Start();
        if (sound == null) return;
        sound.PlaybackSession.PlaybackRate = speed;
        sound.PlaybackSession.Position = Position;
        sound.Play();
    }
}
