using NAudio.CoreAudioApi;
using NAudio.Wave;
using StepRecorder.Core;

namespace StepRecorder.App;

/// <summary>
/// 録画している間の PC の音を録る(WASAPI のループバック)。対象がアプリならそのプロセスと子プロセスの音だけ、モニターなら PC 全体の音。
/// 届いた音は、動画の時計(now)に合わせて Timeline に並べる。now が null の間(一時停止中・録り始める前)の音は捨てる。
/// ponytail: アプリの音は録り始めたときのメインウィンドウのプロセスの木だけ。同じ exe の別プロセス(木の外)が鳴らした音は入らない
/// </summary>
sealed class AudioRecorder : IDisposable
{
    public const int Rate = 48000, Channels = 2, Bits = 16, BlockAlign = Channels * Bits / 8;

    readonly WasapiRecorder recorder;
    public readonly AudioTimeline Timeline = new(Rate, BlockAlign);

    AudioRecorder(WasapiRecorder recorder, Func<TimeSpan?> now)
    {
        this.recorder = recorder;
        recorder.DataAvailable += (buffer, flags, _, _) =>
        {
            if (now() is { } t) Timeline.Add(buffer, t, (flags & AudioClientBufferFlags.Silent) != 0);
        };
        recorder.StartRecording();
    }

    /// <summary>pid のプロセスの木(null なら PC 全体)の音を録り始める。</summary>
    public static AudioRecorder Start(uint? pid, Func<TimeSpan?> now)
    {
        var builder = new WasapiRecorderBuilder().WithFormat(new WaveFormat(Rate, Bits, Channels)); // 共有モードなので Windows が変換してくれる
        builder = pid is { } p ? builder.WithProcessLoopback(p, ProcessLoopbackMode.IncludeTargetProcessTree) : builder.WithLoopbackCapture();
        var recorder = Task.Run(async () => await builder.BuildAsync()).GetAwaiter().GetResult(); // UI スレッドで待つと固まるため
        try { return new(recorder, now); }
        catch
        {
            recorder.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        recorder.StopRecording();
        recorder.Dispose();
    }
}
