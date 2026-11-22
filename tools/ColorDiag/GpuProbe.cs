using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace StepRecorder.ColorDiag;

/// <summary>GPU ドライバーの Video Processor に、色の変換を直接させる。</summary>
static class GpuProbe
{
    /// <summary>
    /// toYuv なら BGRA→NV12、そうでなければ NV12→BGRA。colorSpaces が false なら色空間を指定しない(ドライバーの既定)。
    /// </summary>
    public static Reading Convert(IDXGIAdapter1 adapter, int w, int h, bool toYuv, bool colorSpaces)
    {
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0],
            out ID3D11Device? created, out ID3D11DeviceContext? immediate).CheckError();
        using var device = created!;
        using var context = immediate!;
        using var videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        using var videoContext = context.QueryInterface<ID3D11VideoContext>();

        var (uw, uh) = ((uint)w, (uint)h);
        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(15, 1),
            InputWidth = uw,
            InputHeight = uh,
            OutputFrameRate = new Rational(15, 1),
            OutputWidth = uw,
            OutputHeight = uh,
            Usage = VideoUsage.PlaybackNormal,
        };
        using var enumerator = videoDevice.CreateVideoProcessorEnumerator(content);
        using var processor = videoDevice.CreateVideoProcessor(enumerator, 0);

        var (from, to) = toYuv ? (Format.B8G8R8A8_UNorm, Format.NV12) : (Format.NV12, Format.B8G8R8A8_UNorm);
        using var input = device.CreateTexture2D(new Texture2DDescription(from, uw, uh, 1, 1, BindFlags.RenderTarget));
        using var output = device.CreateTexture2D(new Texture2DDescription(to, uw, uh, 1, 1, BindFlags.RenderTarget));
        var source = toYuv ? Chart.Bgra(w, h) : StepRecorder.Core.Nv12.FromBgra(Chart.Bgra(w, h), w, h);
        var pinned = GCHandle.Alloc(source, GCHandleType.Pinned);
        try { context.UpdateSubresource(input, 0, null, pinned.AddrOfPinnedObject(), toYuv ? uw * 4 : uw, 0); }
        finally { pinned.Free(); }

        using var inputView = videoDevice.CreateVideoProcessorInputView(input, enumerator,
            new VideoProcessorInputViewDescription { ViewDimension = VideoProcessorInputViewDimension.Texture2D });
        using var outputView = videoDevice.CreateVideoProcessorOutputView(output, enumerator,
            new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });

        if (colorSpaces)
        {
            using var v1 = context.QueryInterface<ID3D11VideoContext1>();
            var rgb = ColorSpaceType.RgbFullG22NoneP709;
            var yuv = Core.Nv12.IsBt709(w, h) ? ColorSpaceType.YcbcrStudioG22LeftP709 : ColorSpaceType.YcbcrStudioG22LeftP601;
            v1.VideoProcessorSetStreamColorSpace1(processor, 0, toYuv ? rgb : yuv);
            v1.VideoProcessorSetOutputColorSpace1(processor, toYuv ? yuv : rgb);
        }
        videoContext.VideoProcessorBlt(processor, outputView, 0, 1, [new VideoProcessorStream { Enable = true, InputSurface = inputView }]).CheckError();

        using var staging = device.CreateTexture2D(new Texture2DDescription(to, uw, uh, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        context.CopyResource(staging, output);
        var map = context.Map(staging, 0, MapMode.Read);
        try
        {
            var stride = (int)map.RowPitch;
            var bytes = new byte[stride * (toYuv ? h * 3 / 2 : h)];
            Marshal.Copy(map.DataPointer, bytes, 0, bytes.Length);
            return toYuv ? Chart.FromNv12(bytes, w, h, stride) : Chart.FromBgra(bytes, w, h, stride);
        }
        finally { context.Unmap(staging, 0); }
    }
}
