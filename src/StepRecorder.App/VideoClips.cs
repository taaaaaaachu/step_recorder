using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StepRecorder.Core;
using Windows.Media.Editing;
using Windows.Storage;

namespace StepRecorder.App;

/// <summary>録画した mp4 から、フレームを取り出したり区間を切り出したりする。</summary>
static class VideoClips
{
    const int GifFps = 10, GifMaxWidth = 960; // ponytail: GIF の重さはこの2つで決まる。重すぎるなら設定にする

    /// <summary>time の時点のフレーム。</summary>
    public static BitmapSource Frame(string video, TimeSpan time) => Reader.Open(video).Frame(time);

    /// <summary>動画の長さと大きさ。</summary>
    public static (TimeSpan Duration, int Width, int Height) Info(string video)
    {
        var r = Reader.Open(video);
        return (r.Duration, r.Width, r.Height);
    }

    /// <summary>
    /// start〜end(null なら最後まで)を、crop の範囲だけの mp4 に切り出す。
    /// MediaTranscoder の区間指定(ファイルからの変換)は E_POINTER で失敗するので、
    /// フレームを1枚ずつ取り出して録画と同じ方法でエンコードし直す。
    /// </summary>
    public static void Cut(string video, TimeSpan start, TimeSpan? end, PixelRect? crop, string dst)
    {
        var reader = Reader.Open(video);
        var stop = end ?? reader.Duration;
        var (w, h) = ((crop?.Width ?? reader.Width) & ~1, (crop?.Height ?? reader.Height) & ~1); // H.264 は縦横とも偶数
        IEnumerable<(byte[], TimeSpan)> Frames()
        {
            for (var i = 0; ; i++)
            {
                var t = TimeSpan.FromSeconds((double)i / VideoRecorder.Fps);
                if (start + t >= stop) yield break;
                yield return (BottomUpBgra(Crop(reader.Frame(start + t), crop), w, h), t);
            }
        }
        Task.Run(() => VideoRecorder.Encode(dst, w, h, Frames())).GetAwaiter().GetResult();
    }

    /// <summary>start〜end(null なら最後まで)の crop の範囲を、繰り返し再生する GIF アニメーションにする。</summary>
    public static void ToGif(string video, TimeSpan start, TimeSpan? end, PixelRect? crop, string dst)
    {
        var reader = Reader.Open(video);
        var stop = end ?? reader.Duration;
        var enc = new GifBitmapEncoder();
        for (var i = 0; start + TimeSpan.FromSeconds((double)i / GifFps) < stop; i++)
        {
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

    /// <summary>配布用の書き出しで、動画のステップを区間とトリミングを反映したファイル(と、mp4 ならポスター画像)にする。</summary>
    public static void ExportClip(string dir, Step st, ClipFormat format, string clip, string? poster)
    {
        var src = Path.Combine(dir, st.VideoFile!);
        var start = st.VideoStart ?? TimeSpan.Zero;
        if (format == ClipFormat.Gif) ToGif(src, start, st.VideoEnd, st.Crop, clip);
        else Cut(src, start, st.VideoEnd, st.Crop, clip);
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

    /// <summary>エンコーダーに渡す形(下の行から並んだ BGRA、w×h)にする。</summary>
    static byte[] BottomUpBgra(BitmapSource img, int w, int h)
    {
        var src = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
        var buf = new byte[w * h * 4];
        src.CopyPixels(new Int32Rect(0, 0, w, h), buf, w * 4, 0);
        var flipped = new byte[buf.Length];
        for (var y = 0; y < h; y++) Buffer.BlockCopy(buf, y * w * 4, flipped, (h - 1 - y) * w * 4, w * 4);
        return flipped;
    }

    /// <summary>MediaComposition でフレームを取り出す。</summary>
    sealed class Reader
    {
        readonly MediaComposition composition;
        public readonly int Width, Height;
        public readonly TimeSpan Duration;

        Reader(MediaComposition composition, int width, int height, TimeSpan duration) =>
            (this.composition, Width, Height, Duration) = (composition, width, height, duration);

        public static Reader Open(string video) => Run(async () =>
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(video)); // WinRT は / 区切りを受け付けない
            var props = await file.Properties.GetVideoPropertiesAsync();
            var composition = new MediaComposition();
            composition.Clips.Add(await MediaClip.CreateFromFileAsync(file));
            return new Reader(composition, (int)props.Width, (int)props.Height, props.Duration);
        });

        public BitmapSource Frame(TimeSpan time)
        {
            var bytes = Run(async () =>
            {
                using var stream = (await composition.GetThumbnailAsync(time, Width, Height, VideoFramePrecision.NearestFrame)).AsStream();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                return ms.ToArray();
            });
            // WPF の画像は作ったスレッドに属するので、呼んだ側のスレッドで読み込む
            var img = BitmapFrame.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            img.Freeze();
            return img;
        }
    }

    // WinRT の非同期をスレッドプールで待つ(UI スレッドで待つと固まるため)
    static T Run<T>(Func<Task<T>> f) => Task.Run(f).GetAwaiter().GetResult();
}
