namespace StepRecorder.Core;

/// <summary>
/// 録音した PCM を、動画の時計に合わせて並べる。
/// ループバックは何も鳴っていない間はデータを送ってこないので、その間は無音で埋める(埋めないと音が前に詰まってずれる)。
/// 録音のスレッドが Add し、エンコードのスレッドが Take する。
/// </summary>
public sealed class AudioTimeline(int sampleRate, int blockAlign)
{
    readonly List<byte> pending = []; // まだ Take していない分
    readonly long tolerance = sampleRate / 20; // これ以上遅れて届いたら、間を無音で埋める(届く時刻の揺れは見逃す)
    long written, taken; // 先頭からのサンプル数

    /// <summary>now(動画上の時刻)までに届いた data を足す。data は now で鳴り終えた分とみなす。silent なら中身は無音。</summary>
    public void Add(ReadOnlySpan<byte> data, TimeSpan now, bool silent = false)
    {
        lock (pending)
        {
            var start = (long)(now.TotalSeconds * sampleRate) - data.Length / blockAlign;
            if (start - written > tolerance) Silence(start - written);
            if (silent) pending.AddRange(new byte[data.Length]);
            else pending.AddRange(data);
            written += data.Length / blockAlign;
        }
    }

    /// <summary>until(動画上の時刻)までの、まだ渡していない分。届いていない所は無音。</summary>
    public byte[] Take(TimeSpan until)
    {
        lock (pending)
        {
            var end = (long)(until.TotalSeconds * sampleRate);
            if (end <= taken) return [];
            if (written < end) Silence(end - written); // 遅れて届いた分は、そのあとに続ける
            var bytes = (int)(end - taken) * blockAlign;
            var chunk = pending.GetRange(0, bytes).ToArray();
            pending.RemoveRange(0, bytes);
            taken = end;
            return chunk;
        }
    }

    void Silence(long samples)
    {
        pending.AddRange(new byte[samples * blockAlign]);
        written += samples;
    }
}
