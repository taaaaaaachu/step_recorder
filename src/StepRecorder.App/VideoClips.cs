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

    /// <summary>動画のフレームレート。</summary>
    public static int FrameRate(string video)
    {
        using var reader = Reader.Open(video);
        return reader.Fps;
    }

    /// <summary>音が入っているか。</summary>
    public static bool HasAudio(string video) => Run(() => Task.FromResult(Reader.HasAudio(video)));

    /// <summary>動画の長さと大きさ。</summary>
    public static (TimeSpan Duration, int Width, int Height) Info(string video)
    {
        var info = Run(() => InfoAsync(video));
        return (info.Duration, info.Width, info.Height);
    }

    /// <summary>
    /// start〜end(null なら最後まで)を、crop の範囲だけの mp4 に切り出す。速さは変えない(再生する側で変える)。
    /// fps はフレームレート(null なら元の動画のまま。元より上げても同じコマが並ぶだけ)。
    /// keys・mouse があれば焼き込む(時刻・位置は元の動画のもの)。sound なら、元の動画に音があればその区間の音も入れる。
    /// MediaTranscoder の区間指定(ファイルからの変換)は E_POINTER で失敗するので、
    /// フレームを1枚ずつ取り出して録画と同じ方法でエンコードし直す。
    /// </summary>
    public static void Cut(string video, TimeSpan start, TimeSpan? end, PixelRect? crop, string dst, Action<double>? progress = null,
        IReadOnlyList<KeyChip>? keys = null, IReadOnlyList<MouseMark>? mouse = null, bool sound = false, int? fps = null)
    {
        var burner = new KeyBurner(keys ?? []);
        var clicks = new MouseBurner(mouse ?? []);
        using var reader = Reader.Open(video);
        var rate = fps ?? reader.Fps;
        var stop = end ?? reader.Duration;
        var audio = sound && HasAudio(video) ? Reader.Audio(video, start, stop) : null;
        var (w, h) = ((crop?.Width ?? reader.Width) & ~1, (crop?.Height ?? reader.Height) & ~1); // H.264 は縦横とも偶数
        IEnumerable<(byte[], TimeSpan)> Frames()
        {
            for (var i = 0; ; i++)
            {
                var t = TimeSpan.FromSeconds((double)i / rate);
                if (start + t >= stop) yield break;
                progress?.Invoke(t / (stop - start));
                var bgra = Bgra(Crop(reader.Frame(start + t), crop), w, h);
                clicks.Apply(bgra, w, h, start + t, crop);
                burner.Apply(bgra, w, h, start + t);
                yield return (Nv12.FromBgra(bgra, w, h), t);
            }
        }
        Task.Run(() => VideoRecorder.Encode(dst, w, h, Frames(), audio, rate)).GetAwaiter().GetResult();
    }

    /// <summary>
    /// start〜end(null なら最後まで)の crop の範囲を、speed 倍の速さで繰り返し再生する GIF アニメーションにする。
    /// keys・mouse があれば、縮めたあとの大きさで焼き込む(小さくした文字が潰れないように)。
    /// コマは GifFps(10fps)か fps の少ない方。
    /// </summary>
    public static void ToGif(string video, TimeSpan start, TimeSpan? end, PixelRect? crop, string dst, Action<double>? progress = null, double speed = 1,
        IReadOnlyList<KeyChip>? keys = null, IReadOnlyList<MouseMark>? mouse = null, int fps = GifFps)
    {
        var burner = new KeyBurner(keys ?? []);
        var clicks = new MouseBurner(mouse ?? []);
        using var reader = Reader.Open(video);
        var stop = end ?? reader.Duration;
        var enc = new GifBitmapEncoder();
        // 速ければコマを間引く。遅ければ同じ絵を並べずに 1 コマの表示時間(1/100 秒単位)を延ばす
        var delay = (int)Math.Round(100.0 / Math.Min(GifFps, fps) / Math.Min(speed, 1));
        var step = TimeSpan.FromSeconds(delay / 100.0 * speed);
        for (var at = TimeSpan.Zero; start + at < stop; at += step)
        {
            progress?.Invoke(at / (stop - start));
            BitmapSource f = Crop(reader.Frame(start + at), crop);
            var scale = 1.0;
            if (f.PixelWidth > GifMaxWidth)
            {
                scale = (double)GifMaxWidth / f.PixelWidth;
                f = new TransformedBitmap(f, new ScaleTransform(scale, scale));
            }
            if (!burner.IsEmpty || !clicks.IsEmpty)
            {
                var (w, h) = (f.PixelWidth, f.PixelHeight);
                var bgra = Bgra(f, w, h);
                clicks.Apply(bgra, w, h, start + at, crop, scale);
                burner.Apply(bgra, w, h, start + at);
                f = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, bgra, w * 4);
            }
            enc.Frames.Add(BitmapFrame.Create(f));
        }
        using var ms = new MemoryStream();
        enc.Save(ms);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.WriteAllBytes(dst, Animate(ms.ToArray(), delay));
    }

    /// <summary>
    /// 配布用の書き出しで、動画のステップを区間・トリミング(GIF なら速さも)・キーとマウスの表示を反映したファイル(と、mp4 ならポスター画像)にする。
    /// keys・mouse はそのステップに重ねるもの(KeyOverlay.StrokesFor・MouseOverlay.StrokesFor)。progress には進み具合(0〜1)を知らせる。
    /// </summary>
    public static void ExportClip(string dir, Step st, ClipFormat format, string clip, string? poster, IReadOnlyList<KeyStroke> keys, IReadOnlyList<MouseStroke> mouse,
        Action<double>? progress = null)
    {
        var src = Path.Combine(dir, st.VideoFile!);
        var start = st.VideoStart ?? TimeSpan.Zero;
        var (chips, marks) = (KeyOverlay.Chips(keys), MouseOverlay.Marks(mouse));
        var fps = st.FpsFor(FrameRate(src));
        if (format == ClipFormat.Gif) ToGif(src, start, st.VideoEnd, st.Crop, clip, progress, st.Speed, chips, marks, fps);
        else Cut(src, start, st.VideoEnd, st.Crop, clip, progress, chips, marks, sound: !st.HideAudio, fps: fps);
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

    /// <summary>左上の w×h を BGRA の画素にする。</summary>
    static byte[] Bgra(BitmapSource img, int w, int h)
    {
        var src = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
        var buf = new byte[w * h * 4];
        src.CopyPixels(new Int32Rect(0, 0, w, h), buf, w * 4, 0);
        return buf;
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
        public readonly int Width, Height, Fps;
        public readonly TimeSpan Duration;
        Decoded? current, following;
        Decoded? shown; // DecodeIfChanged が最後に渡したもの
        byte[]? spare; // 読み飛ばしたフレームのバッファ。再生中に大きな確保を繰り返さないよう使い回す
        long asked = long.MaxValue;

        static Reader() => MediaFactory.MFStartup().CheckError();

        Reader(IMFSourceReader source, VideoInfo info, int fps) =>
            (this.source, Width, Height, Duration, Fps) = (source, info.Width, info.Height, info.Duration, fps);

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
                using var type = source.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
                var rate = type.GetUInt64(MediaTypeAttributeKeys.FrameRate); // 上位が分子、下位が分母
                return new Reader(source, info, Math.Max(1, (int)Math.Round((double)(rate >> 32) / Math.Max(1, rate & 0xFFFFFFFF))));
            }
            catch
            {
                source.Dispose();
                throw;
            }
        });

        public void Dispose() => source.Dispose();

        /// <summary>音が入っているか。MTA のスレッドから呼ぶ。</summary>
        public static bool HasAudio(string video)
        {
            using var source = MediaFactory.MFCreateSourceReaderFromURL(Path.GetFullPath(video), null!);
            try
            {
                using var type = source.GetNativeMediaType(SourceReaderIndex.FirstAudioStream, 0);
                return true;
            }
            catch (SharpGen.Runtime.SharpGenException) { return false; } // 音のストリームが無い
        }

        /// <summary>
        /// start〜stop の音(PCM。形式は AudioRecorder のもの。時刻は start から)。読みながら返すので、MTA のスレッドで回す。
        /// </summary>
        public static IEnumerable<(byte[] Pcm, TimeSpan Time)> Audio(string video, TimeSpan start, TimeSpan stop)
        {
            using var source = MediaFactory.MFCreateSourceReaderFromURL(Path.GetFullPath(video), null!);
            source.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            source.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);
            using (var pcm = MediaFactory.MFCreateMediaType())
            {
                pcm.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                pcm.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
                pcm.Set(MediaTypeAttributeKeys.AudioBitsPerSample, AudioRecorder.Bits);
                pcm.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, AudioRecorder.Rate);
                pcm.Set(MediaTypeAttributeKeys.AudioNumChannels, AudioRecorder.Channels);
                source.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, pcm);
            }
            source.SetCurrentPosition(start.Ticks);
            const int Ba = AudioRecorder.BlockAlign;
            var last = (long)((stop - start).TotalSeconds * AudioRecorder.Rate); // start から数えたサンプルの位置
            long next = 0;
            while (next < last)
            {
                using var sample = source.ReadSample(SourceReaderIndex.FirstAudioStream, SourceReaderControlFlag.None, out _, out var flags, out var time);
                if (sample == null)
                {
                    if ((flags & SourceReaderFlag.EndOfStream) != 0) yield break;
                    continue;
                }
                using var buffer = sample.ConvertToContiguousBuffer();
                buffer.Lock(out var data, out _, out var length);
                var bytes = new byte[length];
                try { Marshal.Copy(data, bytes, 0, length); }
                finally { buffer.Unlock(); }
                // 飛んだ先は start より少し前から始まるので、渡し済みの位置より前は捨てる
                var first = (long)Math.Round((time - start.Ticks) / (double)TimeSpan.TicksPerSecond * AudioRecorder.Rate);
                var (from, to) = (Math.Max(0, next - first), Math.Min(length / Ba, last - first));
                if (to <= from) continue;
                yield return (bytes[(int)(from * Ba)..(int)(to * Ba)], TimeSpan.FromSeconds((double)(first + from) / AudioRecorder.Rate));
                next = first + to;
            }
        }

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
