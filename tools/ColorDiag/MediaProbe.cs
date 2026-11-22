using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Vortice.MediaFoundation;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;

namespace StepRecorder.ColorDiag;

/// <summary>Media Foundation の各部品を個別に動かす。</summary>
static class MediaProbe
{
    public static readonly Guid ColorConverter = new("98230571-0087-4204-b020-3282538e57d3");
    public static readonly Guid VideoProcessor = new("88753b26-5b24-49bd-b2e7-0c445c78c982");

    const uint Range0To255 = 1, Range16To235 = 2, Progressive = 2;
    const int OutputProvidesSamples = 0x100;

    /// <summary>変換部品(MFT)に BGRA を直接渡して NV12 にする。tagged なら入出力の範囲を明示する。</summary>
    public static Reading Convert(Guid clsid, int w, int h, bool tagged)
    {
        var instance = Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, throwOnError: true)!)!;
        var unknown = Marshal.GetIUnknownForObject(instance);
        var iid = typeof(IMFTransform).GUID;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var ptr));
        Marshal.Release(unknown);
        using var transform = new IMFTransform(ptr);

        using var input = VideoType(VideoFormatGuids.Rgb32, w, h, tagged ? Range0To255 : null);
        input.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(w * 4));
        using var output = VideoType(VideoFormatGuids.NV12, w, h, tagged ? Range16To235 : null);
        transform.SetInputType(0, input, 0);
        transform.SetOutputType(0, output, 0);

        using var sample = Sample(Chart.Bgra(w, h));
        transform.ProcessInput(0, sample, 0);
        var info = transform.GetOutputStreamInfo(0);
        using var target = (info.Flags & OutputProvidesSamples) != 0 ? null : Sample(new byte[Math.Max(info.Size, w * h * 3 / 2)]);
        var data = new OutputDataBuffer { StreamID = 0, Sample = target! };
        transform.ProcessOutput(ProcessOutputFlags.None, 1, ref data, out _).CheckError();
        return Chart.FromNv12(Bytes(data.Sample), w, h, w);
    }

    static IMFMediaType VideoType(Guid subtype, int w, int h, uint? range)
    {
        var t = MediaFactory.MFCreateMediaType();
        t.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        t.Set(MediaTypeAttributeKeys.Subtype, subtype);
        t.Set(MediaTypeAttributeKeys.FrameSize, (ulong)w << 32 | (uint)h);
        t.Set(MediaTypeAttributeKeys.FrameRate, 15UL << 32 | 1);
        t.Set(MediaTypeAttributeKeys.InterlaceMode, Progressive);
        if (range is { } r) t.Set(MediaTypeAttributeKeys.VideoNominalRange, r);
        return t;
    }

    static IMFSample Sample(byte[] bytes)
    {
        using var buffer = MediaFactory.MFCreateMemoryBuffer(bytes.Length);
        buffer.Lock(out var p, out _, out _);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        buffer.Unlock();
        buffer.CurrentLength = bytes.Length;
        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = 0;
        sample.SampleDuration = TimeSpan.FromSeconds(1.0 / 15).Ticks;
        return sample;
    }

    static byte[] Bytes(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var p, out _, out var length);
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(p, bytes, 0, length);
            return bytes;
        }
        finally { buffer.Unlock(); }
    }

    /// <summary>
    /// 修正前の step_recorder と同じく、BGRA を MediaTranscoder に渡して mp4 にする(変換は Windows に任せる)。
    /// </summary>
    public static async Task EncodeBgra(string path, int w, int h, bool hardware, MediaVideoProcessingAlgorithm algorithm)
    {
        var props = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)w, (uint)h);
        (props.FrameRate.Numerator, props.FrameRate.Denominator) = (15u, 1u);
        var source = new MediaStreamSource(new VideoStreamDescriptor(props)) { BufferTime = TimeSpan.Zero };
        source.Starting += (_, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
        var pixels = Chart.Bgra(w, h);
        var i = 0;
        source.SampleRequested += (_, e) =>
        {
            if (i >= 15) { e.Request.Sample = null; return; }
            var s = MediaStreamSample.CreateFromBuffer(pixels.AsBuffer(), TimeSpan.FromSeconds(i++ / 15.0));
            s.Duration = TimeSpan.FromSeconds(1.0 / 15);
            e.Request.Sample = s;
        };
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
        profile.Audio = null;
        (profile.Video.Width, profile.Video.Height, profile.Video.Bitrate) = ((uint)w, (uint)h, 4_000_000u);
        (profile.Video.FrameRate.Numerator, profile.Video.FrameRate.Denominator) = (15u, 1u);
        await using var file = File.Create(path);
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = hardware, VideoProcessingAlgorithm = algorithm };
        var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, file.AsRandomAccessStream(), profile);
        if (!prepared.CanTranscode) throw new InvalidOperationException($"エンコードできない: {prepared.FailureReason}");
        await prepared.TranscodeAsync();
    }

    /// <summary>mp4 を、色変換せずに(デコーダーの出す NV12 のまま)読む。</summary>
    public static IEnumerable<(byte[] Data, int Width, int Height, int Stride)> RawFrames(string path)
    {
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(Path.GetFullPath(path), null!);
        using (var wanted = MediaFactory.MFCreateMediaType())
        {
            wanted.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            wanted.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, wanted);
        }
        while (true)
        {
            using var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out _);
            if (sample != null)
            {
                using var type = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
                var size = type.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                var (w, h) = ((int)(size >> 32), (int)(size & 0xFFFFFFFF));
                var stride = type.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out var s).Success ? (int)s : w;
                yield return (Bytes(sample), w, h, stride);
            }
            if ((flags & SourceReaderFlag.EndOfStream) != 0) yield break;
        }
    }

    public static Reading ReadChart(string path)
    {
        var f = RawFrames(path).Take(8).Last();
        return Chart.FromNv12(f.Data, f.Width, f.Height, f.Stride);
    }

    /// <summary>録画済みの mp4 のうち、Y が 240 を超える画素の割合(最大 300 フレーム)。</summary>
    public static double Over240Ratio(string path)
    {
        long over = 0, total = 0;
        foreach (var (data, w, h, stride) in RawFrames(path).Take(300))
            for (var y = 0; y < h; y++)
            {
                foreach (var v in data.AsSpan(y * stride, w))
                    if (v > 240) over++;
                total += w;
            }
        return total == 0 ? throw new InvalidDataException("フレームが無い") : (double)over / total;
    }
}
