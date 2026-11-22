using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using StepRecorder.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using WinRT;
using static StepRecorder.App.Native;

namespace StepRecorder.App;

/// <summary>
/// 対象アプリのウィンドウを mp4 に録画する。OBS のウィンドウキャプチャと同じく Windows.Graphics.Capture で
/// ウィンドウごとに録るので、他のアプリが上に重なっても写らない。ダイアログなど同じプロセスのウィンドウは
/// 静止画と同じく Z オーダーの順に重ねる。
/// 画面の大きさは録画開始時のメインウィンドウで固定し、位置はメインウィンドウに追従する。
/// </summary>
sealed class VideoRecorder
{
    public const int Fps = 15;
    const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    readonly nint main;
    readonly bool cursor;
    readonly Func<bool> paused;
    readonly int width, height;

    /// <summary>動画の大きさ。</summary>
    public int Width => width;
    public int Height => height;

    readonly Stopwatch clock = new();
    readonly ManualResetEventSlim started = new();
    readonly BlockingCollection<(byte[] Nv12, TimeSpan Time)> frames = new(boundedCapacity: Fps * 2); // 溜める前に NV12 にする(BGRA の 3/8 の大きさ)
    readonly Thread loop;
    readonly Task encoding;
    volatile bool stopping;
    Exception? error;

    /// <summary>録画開始からの、一時停止を除いた経過時間(動画上の時刻と同じ)。</summary>
    public TimeSpan CurrentTime => clock.Elapsed;

    VideoRecorder(nint main, string path, bool cursor, Func<bool> paused)
    {
        this.main = main;
        this.cursor = cursor;
        this.paused = paused;
        var frame = FrameBounds(main);
        (width, height) = (frame.Width & ~1, frame.Height & ~1); // H.264 は縦横とも偶数
        if (width <= 0 || height <= 0) throw new InvalidOperationException("録画するウィンドウの大きさが取れない");
        encoding = Task.Run(() => Encode(path, width, height, frames.GetConsumingEnumerable()));
        loop = new Thread(CaptureLoop) { IsBackground = true, Name = "video-capture" };
        loop.Start();
        started.Wait(); // 最初のフレームが揃うまで待つ(CurrentTime を操作の時刻と合わせるため)
    }

    /// <summary>
    /// main を含むアプリのウィンドウを path に録画し始める。
    /// paused が true の間は録らず、時計も止める(停止していた時間は動画から詰まる)。
    /// </summary>
    public static VideoRecorder Start(nint main, string path, bool cursor, Func<bool>? paused = null) => new(main, path, cursor, paused ?? (() => false));

    /// <summary>録画を止め、mp4 を書き終えるまで待つ。</summary>
    public void Stop()
    {
        stopping = true;
        loop.Join();
        frames.CompleteAdding();
        encoding.GetAwaiter().GetResult();
        if (error != null) throw new InvalidOperationException("録画に失敗した", error);
    }

    // ---- キャプチャ ----

    void CaptureLoop()
    {
        var sources = new Dictionary<nint, WindowSource>();
        try
        {
            using var gpu = new Gpu();
            // 録り始めは WGC のフレームがまだ届いておらず真っ白になるので、全部のウィンドウの最初のフレームを待ってから時計を動かす
            foreach (var (h, _) in WindowCapture.AppWindows(main))
                if (WindowSource.TryCreate(gpu, h, cursor) is { } s) sources[h] = s;
            var deadline = DateTime.Now.AddSeconds(1);
            while (!sources.Values.All(s => s.Ready()) && DateTime.Now < deadline) Thread.Sleep(10);
            clock.Start();
            started.Set();
            var next = 0L;
            var canvas = new byte[width * height * 4];
            bool Resting() => next > 0 && paused(); // 録り始めの 1 枚は必ず書く(0 枚だとエンコーダーが失敗する)
            while (!stopping)
            {
                var windows = WindowCapture.AppWindows(main);
                foreach (var gone in sources.Keys.Except(windows.Select(w => w.Hwnd)).ToList())
                {
                    sources[gone].Dispose();
                    sources.Remove(gone);
                }
                foreach (var (h, _) in windows)
                    if (!sources.ContainsKey(h) && WindowSource.TryCreate(gpu, h, cursor) is { } s) sources[h] = s;

                if (Resting())
                {
                    clock.Stop();
                    foreach (var s in sources.Values) s.Update(); // 受け取って捨て続ける。溜めると再開したときに停止中の画面が写る
                    Thread.Sleep(1000 / Fps);
                    continue;
                }
                clock.Start();
                // 遅れたらその分のフレームは飛ばす(タイムスタンプは経過時間どおり)
                var index = (long)(clock.Elapsed.TotalSeconds * Fps);
                if (index >= next)
                {
                    var origin = FrameBounds(main);
                    Array.Fill(canvas, (byte)0xFF); // ウィンドウ同士の隙間は白
                    foreach (var (h, r) in windows)
                        if (sources.TryGetValue(h, out var s)) s.DrawTo(canvas, width, height, r.Left - origin.Left, r.Top - origin.Top);
                    if (Resting()) continue; // 合成している間に止められたら、その絵は使わない
                    var frame = (Nv12.FromBgra(canvas, width, height), TimeSpan.FromSeconds((double)index / Fps));
                    while (!frames.TryAdd(frame, 100))
                        if (encoding.IsCompleted) return; // エンコードに失敗して誰も読まない。待ち続けると Stop が返らない
                    next = index + 1;
                }
                var wait = TimeSpan.FromSeconds((double)next / Fps) - clock.Elapsed;
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            }
        }
        catch (Exception e) { error = e; }
        finally
        {
            foreach (var s in sources.Values) s.Dispose();
            started.Set(); // 失敗しても Start を待たせ続けない
        }
    }

    // ---- エンコード ----

    /// <summary>
    /// frames(NV12 と、動画上の時刻)を順に mp4 に書く。録画と切り出しで使う。
    /// 色の変換は Windows に任せない(理由は Nv12 を参照)。
    /// frames は必要になった分だけ読むので、録画中のフレームを待ちながら渡してもよい。
    /// </summary>
    public static async Task Encode(string path, int width, int height, IEnumerable<(byte[] Nv12, TimeSpan Time)> frames)
    {
        var props = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Nv12, (uint)width, (uint)height);
        props.FrameRate.Numerator = Fps;
        props.FrameRate.Denominator = 1;
        var source = new MediaStreamSource(new VideoStreamDescriptor(props)) { BufferTime = TimeSpan.Zero };
        source.Starting += (_, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
        using var next = frames.GetEnumerator();
        source.SampleRequested += (_, e) =>
        {
            // 次のフレームを渡す。無くなったら null を返して終わらせる
            if (!next.MoveNext()) { e.Request.Sample = null; return; }
            var sample = MediaStreamSample.CreateFromBuffer(next.Current.Nv12.AsBuffer(), next.Current.Time);
            sample.Duration = TimeSpan.FromSeconds(1.0 / Fps);
            e.Request.Sample = sample;
        };

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
        profile.Audio = null;
        profile.Video.Width = (uint)width;
        profile.Video.Height = (uint)height;
        profile.Video.FrameRate.Numerator = Fps;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.Bitrate = (uint)Math.Clamp(width * height * Fps / 8, 1_000_000, 12_000_000); // 画面の録画は動きが少ないので控えめ

        // 色の情報。書かないとブラウザーが高さだけで推測して、720 未満の動画の色がずれる(純赤が 248,36,0 になる)
        profile.Video.Properties[MediaTypeAttributeKeys.YuvMatrix] = Nv12.IsBt709(width, height) ? 1u : 2u; // MFVideoTransferMatrix
        profile.Video.Properties[MediaTypeAttributeKeys.VideoNominalRange] = 2u; // 16〜235
        profile.Video.Properties[MediaTypeAttributeKeys.VideoPrimaries] = 2u; // 画面の色(sRGB)は BT.709 と同じ原色
        profile.Video.Properties[MediaTypeAttributeKeys.TransferFunction] = 5u; // BT.709

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        // ハードウェアエンコーダーは小さい画面などで準備に失敗する(0xC00D6D60)ことがあるので、常にソフトウェアで
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = false };
        var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, file.AsRandomAccessStream(), profile);
        if (!prepared.CanTranscode) throw new InvalidOperationException($"エンコードできない: {prepared.FailureReason}");
        await prepared.TranscodeAsync();
    }

    // ---- Direct3D ----

    /// <summary>WGC に渡す Direct3D のデバイス。</summary>
    sealed class Gpu : IDisposable
    {
        public readonly ID3D11Device Device;
        public readonly ID3D11DeviceContext Context;
        public readonly IDirect3DDevice WinRT;

        public Gpu()
        {
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0], out Device!, out Context!).CheckError();
            using var dxgi = Device.QueryInterface<IDXGIDevice>();
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var ptr));
            WinRT = MarshalInterface<IDirect3DDevice>.FromAbi(ptr);
            Marshal.Release(ptr);
        }

        public void Dispose()
        {
            (WinRT as IDisposable)?.Dispose();
            Context.Dispose();
            Device.Dispose();
        }
    }

    /// <summary>1つのウィンドウの WGC。最後に届いたフレームの画素を持っておく。</summary>
    sealed class WindowSource : IDisposable
    {
        readonly Gpu gpu;
        readonly Direct3D11CaptureFramePool pool;
        readonly GraphicsCaptureSession session;
        SizeInt32 poolSize;
        ID3D11Texture2D? staging;
        byte[] pixels = [];
        int w, h;

        WindowSource(Gpu gpu, GraphicsCaptureItem item, bool cursor)
        {
            this.gpu = gpu;
            poolSize = item.Size;
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(gpu.WinRT, PixelFormat, 2, poolSize);
            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = cursor;
            try { session.IsBorderRequired = false; } catch (Exception) { } // 録画中の黄色い枠。消せない環境もある
            session.StartCapture();
        }

        public static WindowSource? TryCreate(Gpu gpu, nint hwnd, bool cursor)
        {
            try { return new(gpu, CreateItem(hwnd), cursor); }
            catch (Exception e) when (e is COMException or ArgumentException) { return null; } // 録れないウィンドウ(ツールチップの一部など)は飛ばす
        }

        /// <summary>
        /// 最新のフレームを、合成先(上の行から並んだ BGRA)の (x, y) に描く。まだ1枚も届いていなければ何もしない。
        /// </summary>
        public void DrawTo(byte[] canvas, int cw, int ch, int x, int y)
        {
            Update();
            var from = Math.Max(0, -x);
            var count = Math.Min(w, cw - x) - from;
            if (count <= 0) return;
            for (var row = Math.Max(0, -y); row < h && y + row < ch; row++)
                Buffer.BlockCopy(pixels, (row * w + from) * 4, canvas, ((y + row) * cw + x + from) * 4, count * 4);
        }

        /// <summary>最初のフレームが届いたか。</summary>
        public bool Ready()
        {
            Update();
            return pixels.Length > 0;
        }

        /// <summary>届いているうちの最新のフレームを受け取る。</summary>
        public void Update()
        {
            var latest = pool.TryGetNextFrame();
            if (latest == null) return;
            while (pool.TryGetNextFrame() is { } newer)
            {
                latest.Dispose();
                latest = newer;
            }
            using var frame = latest;
            if (frame.ContentSize.Width != poolSize.Width || frame.ContentSize.Height != poolSize.Height)
            {
                poolSize = frame.ContentSize;
                pool.Recreate(gpu.WinRT, PixelFormat, 2, poolSize); // 大きさが変わったら次のフレームから合わせる
            }

            using var texture = TextureOf(frame.Surface);
            var desc = texture.Description;
            if (staging == null || staging.Description.Width != desc.Width || staging.Description.Height != desc.Height)
            {
                staging?.Dispose();
                staging = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, desc.Width, desc.Height, 1, 1,
                    BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            }
            gpu.Context.CopyResource(staging, texture);

            (w, h) = (Math.Min(frame.ContentSize.Width, (int)desc.Width), Math.Min(frame.ContentSize.Height, (int)desc.Height));
            if (pixels.Length != w * h * 4) pixels = new byte[w * h * 4];
            var map = gpu.Context.Map(staging, 0, MapMode.Read);
            try
            {
                for (var row = 0; row < h; row++)
                    Marshal.Copy(map.DataPointer + row * (nint)map.RowPitch, pixels, row * w * 4, w * 4);
            }
            finally { gpu.Context.Unmap(staging, 0); }
        }

        public void Dispose()
        {
            session.Dispose();
            pool.Dispose();
            staging?.Dispose();
        }
    }

    // ---- WinRT と COM のつなぎ ----

    static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    static readonly Guid CaptureItemInteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    static readonly Guid DxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    static unsafe GraphicsCaptureItem CreateItem(nint hwnd)
    {
        using var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = QueryInterface(factory.ThisPtr, CaptureItemInteropIid);
        try
        {
            // IGraphicsCaptureItemInterop::CreateForWindow (vtable 3)
            var iid = GraphicsCaptureItemIid;
            nint ptr;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(*(nint**)interop + 3)))(interop, hwnd, &iid, &ptr));
            var item = GraphicsCaptureItem.FromAbi(ptr);
            Marshal.Release(ptr);
            return item;
        }
        finally { Marshal.Release(interop); }
    }

    static unsafe ID3D11Texture2D TextureOf(IDirect3DSurface surface)
    {
        var access = QueryInterface(((IWinRTObject)surface).NativeObject.ThisPtr, DxgiInterfaceAccessIid);
        try
        {
            // IDirect3DDxgiInterfaceAccess::GetInterface (vtable 3)
            var iid = typeof(ID3D11Texture2D).GUID;
            nint ptr;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(*(nint**)access + 3)))(access, &iid, &ptr));
            return new ID3D11Texture2D(ptr);
        }
        finally { Marshal.Release(access); }
    }

    static nint QueryInterface(nint unknown, Guid iid)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var ptr));
        return ptr;
    }

    [DllImport("d3d11.dll")] static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}
