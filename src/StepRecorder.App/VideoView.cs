using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Core;
using MediaPlayer = Windows.Media.Playback.MediaPlayer;
using MediaPlaybackState = Windows.Media.Playback.MediaPlaybackState;

namespace StepRecorder.App;

/// <summary>
/// 動画の表示と再生。書き出しと同じ方法で復号した画像を出す。
/// MediaElement は明るさの範囲をドライバーに伝えないので、NVIDIA の既定の設定では 16〜235 のまま出て眠い色になる。
/// 音のある動画は、同じファイルを MediaPlayer で鳴らし、再生中はその再生位置を映像の時刻にする(音を時計にするので、長く再生してもずれない)。
/// 音の無い動画は Stopwatch を時計にする。
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
        public volatile bool HasAudio;
    }

    static readonly TimeSpan Settle = TimeSpan.FromSeconds(0.5); // 音の再生位置が飛んだ先からこれ以上離れていれば、まだ反映されていない

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
    bool soundSettled; // 再生を始めた・飛んだあと、音の再生位置がそこに追いついたか。追いつくまでは飛んだ所で待つ

    /// <summary>今、音の再生位置を時計にしているか。</summary>
    internal bool FollowsAudio => clock.IsRunning && sound != null && soundSettled;

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
            var at = SoundPosition() ?? origin + clock.Elapsed * speed;
            return duration is { } end && at > end ? end : at;
        }
        set
        {
            origin = value;
            if (clock.IsRunning) clock.Restart();
            else clock.Reset();
            if (clock.IsRunning && sound != null)
            {
                soundSettled = false;
                sound.PlaybackSession.Position = value;
            }
            Request(value);
        }
    }

    /// <summary>再生中の、音の再生位置。音が無い・止まっているなら null。鳴り始めるまで・飛んだ先が反映されるまでは飛んだ所。</summary>
    TimeSpan? SoundPosition()
    {
        if (!clock.IsRunning || sound?.PlaybackSession is not { } session) return null;
        var at = session.Position;
        if (!soundSettled && session.PlaybackState == MediaPlaybackState.Playing && (at - origin).Duration() < Settle) soundSettled = true;
        return soundSettled ? at : origin;
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
        var at = Position; // 音を止める前に読む
        playing = false;
        timer.Stop();
        sound?.Pause();
        clock.Reset();
        Position = at; // 止めた所のフレームにする
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
            p.HasAudio = VideoClips.Reader.HasAudio(p.Path);
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
        if (p.HasAudio)
        {
            sound = new MediaPlayer { AutoPlay = false, IsMuted = muted, Source = MediaSource.CreateFromUri(new Uri(p.Path)) };
            sound.CommandManager.IsEnabled = false; // Windows のメディアの操作(音量を変えたときに出る表示など)に出さない
        }
        if (playing) StartClock();
    }

    void StartClock()
    {
        clock.Start();
        if (sound == null) return;
        soundSettled = false;
        sound.PlaybackSession.PlaybackRate = speed;
        sound.PlaybackSession.Position = Position;
        sound.Play();
    }
}
