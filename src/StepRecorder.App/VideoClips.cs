using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StepRecorder.Core;
using Vortice.MediaFoundation;
using Windows.Storage;

namespace StepRecorder.App;

/// <summary>録画した mp4 から、フレームを取り出したり区間を切り出したりする。</summary>
static class VideoClips
{
    const int GifFps = 10, GifMaxWidth = 960; // ponytail: GIF の重さはこの2つで決まる。重すぎるなら設定にする

    /// <summary>time の時点のフレーム。</summary>
    public static BitmapSource Frame(string video, TimeSpan time)
    {
        using var reader = Reader.Open(video);
        return reader.Frame(time);
    }

    /// <summary>動画の長さと大きさ。</summary>
    public static (TimeSpan Duration, int Width, int Height) Info(string video)
    {
        var info = Run(() => InfoAsync(video));
        return (info.Duration, info.Width, info.Height);
    }

    /// <summary>
    /// start〜end(null なら最後まで)を、crop の範囲だけの mp4 に切り出す。
    /// MediaTranscoder の区間指定(ファイルからの変換)は E_POINTER で失敗するので、
    /// フレームを1枚ずつ取り出して録画と同じ方法でエンコードし直す。
    /// </summary>
    public static void Cut(string video, TimeSpan start, TimeSpan? end, PixelRect? crop, string dst, Action<double>? progress = null)
    {
        using var reader = Reader.Open(video);
        var stop = end ?? reader.Duration;
        var (w, h) = ((crop?.Width ?? reader.Width) & ~1, (crop?.Height ?? reader.Height) & ~1); // H.264 は縦横とも偶数
        IEnumerable<(byte[], TimeSpan)> Frames()
        {
            for (var i = 0; ; i++)
            {
                var t = TimeSpan.FromSeconds((double)i / VideoRecorder.Fps);
                if (start + t >= stop) yield break;
                progress?.Invoke(t / (stop - start));
                yield return (ToNv12(Crop(reader.Frame(start + t), crop), w, h), t);
            }
        }
        Task.Run(() => VideoRecorder.Encode(dst, w, h, Frames())).GetAwaiter().GetResult();
    }

    /// <summary>start〜end(null なら最後まで)の crop の範囲を、繰り返し再生する GIF アニメーションにする。</summary>
    public static void ToGif(string video, TimeSpan start, TimeSpan? end, PixelRect? crop, string dst, Action<double>? progress = null)
    {
        using var reader = Reader.Open(video);
        var stop = end ?? reader.Duration;
        var enc = new GifBitmapEncoder();
        for (var i = 0; start + TimeSpan.FromSeconds((double)i / GifFps) < stop; i++)
        {
            progress?.Invoke(TimeSpan.FromSeconds((double)i / GifFps) / (stop - start));
            BitmapSource f = Crop(reader.Frame(start + TimeSpan.FromSeconds((double)i / GifFps)), crop);
            if (f.PixelWidth > GifMaxWidth)
            {
                var scale = (double)GifMaxWidth / f.PixelWidth;
                f = new TransformedBitmap(f, new ScaleTransform(scale, scale));
            }
            enc.Frames.Add(BitmapFrame.Create(f));
        }
        using var ms = new MemoryStream();
        enc.Save(ms);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.WriteAllBytes(dst, Animate(ms.ToArray(), 100 / GifFps));
    }

    /// <summary>
    /// 配布用の書き出しで、動画のステップを区間とトリミングを反映したファイル(と、mp4 ならポスター画像)にする。
    /// progress には進み具合(0〜1)を知らせる。
    /// </summary>
    public static void ExportClip(string dir, Step st, ClipFormat format, string clip, string? poster, Action<double>? progress = null)
    {
        var src = Path.Combine(dir, st.VideoFile!);
        var start = st.VideoStart ?? TimeSpan.Zero;
        if (format == ClipFormat.Gif) ToGif(src, start, st.VideoEnd, st.Crop, clip, progress);
        else Cut(src, start, st.VideoEnd, st.Crop, clip, progress);
        if (poster != null) WindowCapture.SavePng(Frame(clip, TimeSpan.Zero), poster);
    }

    static BitmapSource Crop(BitmapSource f, PixelRect? crop) =>
        crop is { } c ? new CroppedBitmap(f, new Int32Rect(c.X, c.Y, Math.Min(c.Width, f.PixelWidth - c.X), Math.Min(c.Height, f.PixelHeight - c.Y))) : f;

    /// <summary>
    /// GifBitmapEncoder は表示時間も繰り返しの指定も書けない(メタデータを渡しても無視される)ので、
    /// 書き出した GIF のブロックをたどって、先頭に繰り返し(NETSCAPE2.0)を、各画像の前に表示時間(1/100 秒単位)を入れる。
    /// </summary>
    internal static byte[] Animate(byte[] gif, int delay)
    {
        var flags = gif[10];
        var at = 13 + ((flags & 0x80) != 0 ? 3 << ((flags & 7) + 1) : 0); // ヘッダ + 論理画面記述子 + グローバルカラーテーブル
        var output = new List<byte>(gif.Length + 1024);
        output.AddRange(gif[..at]);
        output.AddRange([0x21, 0xFF, 0x0B, .."NETSCAPE2.0"u8, 0x03, 0x01, 0x00, 0x00, 0x00]);
        while (at < gif.Length)
        {
            var start = at;
            switch (gif[at])
            {
                case 0x21: // 拡張ブロック。既にある表示時間の指定(F9)は捨てて入れ直す
                    var label = gif[at + 1];
                    at = SkipSubBlocks(gif, at + 2);
                    if (label != 0xF9) output.AddRange(gif[start..at]);
                    break;
                case 0x2C: // 画像
                    var local = gif[at + 9];
                    at += 10 + ((local & 0x80) != 0 ? 3 << ((local & 7) + 1) : 0) + 1; // 画像記述子 + ローカルカラーテーブル + LZW の最小符号長
                    at = SkipSubBlocks(gif, at);
                    output.AddRange([0x21, 0xF9, 0x04, 0x00, (byte)delay, (byte)(delay >> 8), 0x00, 0x00]);
                    output.AddRange(gif[start..at]);
                    break;
                default: // 0x3B(終わり)
                    output.AddRange(gif[at..]);
                    at = gif.Length;
                    break;
            }
        }
        return [.. output];
    }

    static int SkipSubBlocks(byte[] gif, int at)
    {
        while (gif[at] != 0) at += gif[at] + 1;
        return at + 1;
    }

    /// <summary>エンコーダーに渡す形(NV12、w×h)にする。</summary>
    static byte[] ToNv12(BitmapSource img, int w, int h)
    {
        var src = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
        var buf = new byte[w * h * 4];
        src.CopyPixels(new Int32Rect(0, 0, w, h), buf, w * 4, 0);
        return Nv12.FromBgra(buf, w, h);
    }

    /// <summary>
    /// フレームを取り出す。デコーダーから NV12 のまま受け取り、RGB には自分で戻す(理由は Nv12 を参照)。
    /// 時刻の昇順に頼まれたら続きから読むので速い。ファイルを開いたままなので Dispose すること。
    /// </summary>
    internal sealed class Reader : IDisposable
    {
        sealed record Decoded(long Time, byte[] Nv12, int Stride, int Rows);

        static readonly long Tie = TimeSpan.FromMilliseconds(0.1).Ticks; // 時刻の丸めの誤差
        static readonly long ReadOnLimit = TimeSpan.FromSeconds(1.5).Ticks; // これより先は読み進めずに飛ぶ(キーフレームは 1 秒おき)
        static readonly long SeekMargin = TimeSpan.FromSeconds(0.2).Ticks; // 表示順と復号順のずれの分、手前へ飛ぶ

        readonly IMFSourceReader source;
        public readonly int Width, Height;
        public readonly TimeSpan Duration;
        Decoded? current, following;
        Decoded? shown; // DecodeIfChanged が最後に渡したもの
        byte[]? spare; // 読み飛ばしたフレームのバッファ。再生中に大きな確保を繰り返さないよう使い回す
        long asked = long.MaxValue;

        static Reader() => MediaFactory.MFStartup().CheckError();

        Reader(IMFSourceReader source, VideoInfo info) =>
            (this.source, Width, Height, Duration) = (source, info.Width, info.Height, info.Duration);

        public static Reader Open(string video) => Run(async () =>
        {
            var info = await InfoAsync(video);
            var source = MediaFactory.MFCreateSourceReaderFromURL(Path.GetFullPath(video), null!);
            try
            {
                using var nv12 = MediaFactory.MFCreateMediaType();
                nv12.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                nv12.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                source.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, nv12);
                return new Reader(source, info);
            }
            catch
            {
                source.Dispose();
                throw;
            }
        });

        public void Dispose() => source.Dispose();

        /// <summary>time に最も近いフレーム(同じ近さなら前の方)。終わりを過ぎていれば最後のフレーム。</summary>
        public BitmapSource Frame(TimeSpan time)
        {
            // Media Foundation は MTA のスレッドで呼ぶ
            var bgra = Run(() => Task.FromResult(Decode(time.Ticks)));
            // WPF の画像は作ったスレッドに属するので、呼んだ側のスレッドで作る
            var img = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr32, null, bgra, Width * 4);
            img.Freeze();
            return img;
        }

        byte[] Decode(long time)
        {
            MoveTo(time);
            return Nv12.ToBgra(current!.Nv12, current.Stride, current.Rows, Width, Height);
        }

        /// <summary>time のフレームが前回渡したものと違えば bgra に書いて true。MTA のスレッドから呼ぶ(再生用)。</summary>
        public bool DecodeIfChanged(long time, byte[] bgra)
        {
            MoveTo(time);
            if (ReferenceEquals(shown, current)) return false;
            shown = current;
            Nv12.ToBgra(current!.Nv12, current.Stride, current.Rows, Width, Height, bgra);
            return true;
        }

        void MoveTo(long time)
        {
            time = Math.Clamp(time, 0, Duration.Ticks);
            if (current == null || time < asked || time > current.Time + ReadOnLimit) Seek(time);
            asked = time;
            while ((following ??= Read()) is { } next && Math.Abs(next.Time - time) + Tie < Math.Abs(current!.Time - time))
            {
                spare = current!.Nv12;
                (current, following) = (next, null);
            }
        }

        void Seek(long time)
        {
            following = null;
            // 飛んだ先が time より後か、何も読めなければ(終わり近く)、もっと手前から読み直す
            for (var back = SeekMargin; ; back *= 8)
            {
                var at = Math.Max(0, time - back);
                source.SetCurrentPosition(at);
                current = Read();
                if (current != null && (current.Time <= time || at == 0)) return;
                if (current == null && at == 0) throw new InvalidOperationException("動画にフレームが無い");
            }
        }

        Decoded? Read()
        {
            while (true)
            {
                using var sample = source.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out var time);
                if (sample != null)
                {
                    using var type = source.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
                    var size = type.GetUInt64(MediaTypeAttributeKeys.FrameSize); // 切り上げられた大きさ
                    var stride = type.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out var s).Success ? (int)s : (int)(size >> 32);
                    using var buffer = sample.ConvertToContiguousBuffer();
                    buffer.Lock(out var data, out _, out var length);
                    try
                    {
                        var bytes = spare?.Length == length ? spare : new byte[length];
                        spare = null;
                        Marshal.Copy(data, bytes, 0, length);
                        return new(time, bytes, stride, (int)(size & 0xFFFFFFFF));
                    }
                    finally { buffer.Unlock(); }
                }
                if ((flags & SourceReaderFlag.EndOfStream) != 0) return null;
            }
        }
    }

    sealed record VideoInfo(int Width, int Height, TimeSpan Duration);

    static async Task<VideoInfo> InfoAsync(string video)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(video)); // WinRT は / 区切りを受け付けない
        var props = await file.Properties.GetVideoPropertiesAsync();
        return new((int)props.Width, (int)props.Height, props.Duration);
    }

    // WinRT の非同期をスレッドプールで待つ(UI スレッドで待つと固まるため)
    static T Run<T>(Func<Task<T>> f) => Task.Run(f).GetAwaiter().GetResult();
}
