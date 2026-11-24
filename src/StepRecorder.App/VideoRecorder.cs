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
/// 対象アプリのウィンドウか、モニターを mp4 に録画する(Windows.Graphics.Capture)。
/// アプリは OBS のウィンドウキャプチャと同じくウィンドウごとに録るので、他のアプリが上に重なっても写らない。
/// ダイアログなど同じアプリのウィンドウは静止画と同じく Z オーダーの順に重ねる。
/// 画面の大きさは録画開始時のメインウィンドウで固定し、位置は今のメインウィンドウに追従する(AppProcesses.Follow)。
/// モニターは 1 枚を丸ごと録る。大きさと位置は録画開始時のまま。
/// 音も録る(AudioRecorder)。録れなければ映像だけにし、理由を AudioFailure に残す。
/// </summary>
sealed class VideoRecorder
{
    const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    /// <summary>何を録るか。Origin が動画に当たるスクリーン上の範囲で、そこへ Items(奥から順)を重ねる。Blend なら透明度を見て重ねる(ウィンドウ)。</summary>
    sealed record Input(Func<RECT> Origin, Func<List<(nint Handle, RECT Rect)>> Items, Func<nint, GraphicsCaptureItem> CreateItem, bool Blend);

    readonly Input input;
    readonly bool cursor;
    readonly Func<bool> paused;
    readonly int width, height, fps;

    /// <summary>動画の大きさ。</summary>
    public int Width => width;
    public int Height => height;

    readonly Stopwatch clock = new();
    readonly ManualResetEventSlim started = new();
    readonly BlockingCollection<(byte[] Nv12, TimeSpan Time)> frames; // 溜める前に NV12 にする(BGRA の 3/8 の大きさ)
    readonly Thread loop;
    readonly Task encoding;
    readonly AudioRecorder? audio;
    volatile bool stopping;
    long stoppedAt; // 止めたときの動画上の時刻(Ticks)。音はここまで書く
    volatile object origin; // 動画に当たるスクリーン上の範囲(RECT)。録画のスレッドが更新し、ToVideo が読む
    Exception? error;

    /// <summary>録画開始からの、一時停止を除いた経過時間(動画上の時刻と同じ)。</summary>
    public TimeSpan CurrentTime => clock.Elapsed;

    /// <summary>音を録れなかった理由。録れていれば null。</summary>
    public Exception? AudioFailure { get; }

    /// <summary>audioPid はそのプロセスの木の音を録る。null なら PC 全体の音。fps はフレームレート。</summary>
    VideoRecorder(Input input, string path, bool cursor, Func<bool> paused, uint? audioPid, int fps)
    {
        this.input = input;
        this.fps = fps;
        frames = new(boundedCapacity: fps * 2);
        this.cursor = cursor;
        this.paused = paused;
        var frame = input.Origin();
        origin = frame;
        (width, height) = (frame.Width & ~1, frame.Height & ~1); // H.264 は縦横とも偶数
        if (width <= 0 || height <= 0) throw new InvalidOperationException("録画するものの大きさが取れない");
        try { audio = AudioRecorder.Start(audioPid, () => clock.IsRunning && !paused() ? clock.Elapsed : null); }
        catch (Exception e) { AudioFailure = e; } // 古い Windows・音の出る機器が無いなど。映像は録る
        encoding = Task.Run(() => Encode(path, width, height, frames.GetConsumingEnumerable(), audio == null ? null : AudioChunks(audio), fps));
        loop = new Thread(CaptureLoop) { IsBackground = true, Name = "video-capture" };
        loop.Start();
        started.Wait(); // 最初のフレームが揃うまで待つ(CurrentTime を操作の時刻と合わせるため)
    }

    /// <summary>
    /// main を含むアプリのウィンドウを path に録画し始める。
    /// paused が true の間は録らず、時計も止める(停止していた時間は動画から詰まる)。
    /// </summary>
    public static VideoRecorder Start(nint main, string path, bool cursor, Func<bool>? paused = null, AppProcesses? app = null, int fps = FrameRate.Default)
    {
        app ??= new AppProcesses(ProcessId(main));
        var (current, className, origin) = (main, ClassName(main), FrameBounds(main));
        // Items を先に呼ぶ(そこで追う相手を決める)
        List<(nint, RECT)> Items()
        {
            current = app.Follow(current, className, GetAncestor(GetForegroundWindow(), GA_ROOT));
            return WindowCapture.AppWindows(current, app);
        }
        RECT Origin()
        {
            if (IsWindow(current)) origin = FrameBounds(current); // 閉じられて代わりも無ければ、最後の位置のまま
            return origin;
        }
        return new(new(Origin, Items, CreateWindowItem, true), path, cursor, paused ?? (() => false), app.Pid, fps);
    }

    /// <summary>モニターに見えているものを丸ごと path に録画し始める。paused は Start と同じ。</summary>
    public static VideoRecorder StartMonitor(nint monitor, RECT bounds, string path, bool cursor, Func<bool>? paused = null, int fps = FrameRate.Default) =>
        new(new(() => bounds, () => [(monitor, bounds)], CreateMonitorItem, false), path, cursor, paused ?? (() => false), null, fps);

    /// <summary>スクリーン上の点の、動画上の位置。動画の外なら null。</summary>
    public PixelPoint? ToVideo(POINT p)
    {
        var o = (RECT)origin;
        var (x, y) = (p.X - o.Left, p.Y - o.Top);
        return x >= 0 && y >= 0 && x < width && y < height ? new(x, y) : null;
    }

    /// <summary>録画を止め、mp4 を書き終えるまで待つ。</summary>
    public void Stop()
    {
        Interlocked.Exchange(ref stoppedAt, clock.Elapsed.Ticks);
        stopping = true;
        loop.Join();
        audio?.Dispose();
        frames.CompleteAdding();
        Task.WaitAny(encoding);
        if (error != null) throw new InvalidOperationException("録画に失敗した", error); // エンコードの失敗(フレームが無い)より、その原因を伝える
        encoding.GetAwaiter().GetResult();
    }

    // ---- キャプチャ ----

    void CaptureLoop()
    {
        var sources = new Dictionary<nint, CaptureSource>();
        try
        {
            using var gpu = new Gpu();
            // 録り始めは WGC のフレームがまだ届いておらず真っ白になるので、全部のウィンドウの最初のフレームを待ってから時計を動かす
            foreach (var (h, _) in input.Items())
                if (CaptureSource.TryCreate(gpu, h, input.CreateItem, cursor) is { } s) sources[h] = s;
            var deadline = DateTime.Now.AddSeconds(1);
            while (!sources.Values.All(s => s.Ready()) && DateTime.Now < deadline) Thread.Sleep(10);
            clock.Start();
            started.Set();
            var next = 0L;
            var canvas = new byte[width * height * 4];
            bool Resting() => next > 0 && paused(); // 録り始めの 1 枚は必ず書く(0 枚だとエンコーダーが失敗する)
            while (!stopping || next == 0) // 最初の周回より先に止められても 1 枚は書く
            {
                var items = input.Items();
                foreach (var gone in sources.Keys.Except(items.Select(i => i.Handle)).ToList())
                {
                    sources[gone].Dispose();
                    sources.Remove(gone);
                }
                foreach (var (h, _) in items)
                    if (!sources.ContainsKey(h) && CaptureSource.TryCreate(gpu, h, input.CreateItem, cursor) is { } s) sources[h] = s;

                if (Resting())
                {
                    clock.Stop();
                    foreach (var s in sources.Values) s.Update(); // 受け取って捨て続ける。溜めると再開したときに停止中の画面が写る
                    Thread.Sleep(1000 / fps);
                    continue;
                }
                clock.Start();
                // 遅れたらその分のフレームは飛ばす(タイムスタンプは経過時間どおり)
                var index = (long)(clock.Elapsed.TotalSeconds * fps);
                // タブの切り替え直後は、新しいウィンドウの絵が届くまで(長くても 1 秒)待つ。白いコマを挟まない
                var arriving = next > 0 && items.Count > 0 && index - next < fps
                    && !items.Any(i => sources.TryGetValue(i.Handle, out var s) && s.Ready());
                if (arriving) Thread.Sleep(10);
                else if (index >= next)
                {
                    var at = input.Origin();
                    origin = at;
                    Array.Fill(canvas, (byte)0xFF); // ウィンドウ同士の隙間は白
                    foreach (var (h, r) in items)
                        if (sources.TryGetValue(h, out var s)) s.DrawTo(canvas, width, height, r.Left - at.Left, r.Top - at.Top, input.Blend);
                    if (Resting()) continue; // 合成している間に止められたら、その絵は使わない
                    var frame = (Nv12.FromBgra(canvas, width, height), TimeSpan.FromSeconds((double)index / fps));
                    while (!frames.TryAdd(frame, 100))
                        if (encoding.IsCompleted) return; // エンコードに失敗して誰も読まない。待ち続けると Stop が返らない
                    next = index + 1;
                }
                var wait = TimeSpan.FromSeconds((double)next / fps) - clock.Elapsed;
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

    // ---- 音 ----

    static readonly TimeSpan AudioChunk = TimeSpan.FromSeconds(0.1);
    static readonly TimeSpan AudioLag = TimeSpan.FromSeconds(0.3); // 鳴ってから届くまでの遅れ。これだけ待ってから渡す

    /// <summary>録った音を 0.1 秒ずつ渡す。届くのを待ちながら渡すので、止めるまで終わらない。</summary>
    IEnumerable<(byte[] Pcm, TimeSpan Time)> AudioChunks(AudioRecorder audio)
    {
        for (var at = TimeSpan.Zero; ; at += AudioChunk)
        {
            while (!stopping && clock.Elapsed < at + AudioChunk + AudioLag) Thread.Sleep(10); // 一時停止中は時計が止まっているので待ち続ける
            var end = stopping ? TimeSpan.FromTicks(Interlocked.Read(ref stoppedAt)) : TimeSpan.MaxValue;
            if (at >= end) yield break;
            yield return (audio.Timeline.Take(at + AudioChunk < end ? at + AudioChunk : end), at);
        }
    }

    // ---- エンコード ----

    /// <summary>
    /// frames(NV12 と、動画上の時刻)を順に mp4 に書く。録画と切り出しで使う。
    /// 色の変換は Windows に任せない(理由は Nv12 を参照)。
    /// frames は必要になった分だけ読むので、録画中のフレームを待ちながら渡してもよい。
    /// audio(PCM と、動画上の時刻。形式は AudioRecorder のもの)があれば AAC で入れる。fps はフレームレート(frames の時刻の間隔と揃える)。
    /// </summary>
    public static async Task Encode(string path, int width, int height, IEnumerable<(byte[] Nv12, TimeSpan Time)> frames,
        IEnumerable<(byte[] Pcm, TimeSpan Time)>? audio = null, int fps = FrameRate.Default)
    {
        var props = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Nv12, (uint)width, (uint)height);
        props.FrameRate.Numerator = (uint)fps;
        props.FrameRate.Denominator = 1;
        var video = new VideoStreamDescriptor(props);
        var sound = new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm(AudioRecorder.Rate, AudioRecorder.Channels, AudioRecorder.Bits));
        var source = audio == null ? new MediaStreamSource(video) : new MediaStreamSource(video, sound);
        source.BufferTime = TimeSpan.Zero;
        source.Starting += (_, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
        using var nextFrame = frames.GetEnumerator();
        using var nextSound = audio?.GetEnumerator();
        source.SampleRequested += (_, e) =>
        {
            // 次のフレーム(音)を渡す。無くなったら null を返して終わらせる
            var isSound = ReferenceEquals(e.Request.StreamDescriptor, sound); // 同じストリームか
            if (!(isSound ? nextSound!.MoveNext() : nextFrame.MoveNext())) { e.Request.Sample = null; return; }
            var (data, time) = isSound ? nextSound!.Current : nextFrame.Current;
            var sample = MediaStreamSample.CreateFromBuffer(data.AsBuffer(), time);
            sample.Duration = isSound ? TimeSpan.FromSeconds((double)data.Length / AudioRecorder.BlockAlign / AudioRecorder.Rate) : TimeSpan.FromSeconds(1.0 / fps);
            e.Request.Sample = sample;
        };

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
        profile.Audio = audio == null ? null : AudioEncodingProperties.CreateAac(AudioRecorder.Rate, AudioRecorder.Channels, 128_000);
        profile.Video.Width = (uint)width;
        profile.Video.Height = (uint)height;
        profile.Video.FrameRate.Numerator = (uint)fps;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.Bitrate = (uint)Math.Clamp(width * height * fps / 8, 1_000_000, 12_000_000); // 画面の録画は動きが少ないので控えめ

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

    /// <summary>1つのウィンドウかモニターの WGC。最後に届いたフレームの画素を持っておく。</summary>
    sealed class CaptureSource : IDisposable
    {
        readonly Gpu gpu;
        readonly Direct3D11CaptureFramePool pool;
        readonly GraphicsCaptureSession session;
        SizeInt32 poolSize;
        ID3D11Texture2D? staging;
        byte[] pixels = [];
        int w, h;

        CaptureSource(Gpu gpu, GraphicsCaptureItem item, bool cursor)
        {
            this.gpu = gpu;
            poolSize = item.Size;
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(gpu.WinRT, PixelFormat, 2, poolSize);
            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = cursor;
            // 録画中の黄色い枠。Windows 10 2004 では消せない(Windows 11 などで使える)。使えるはずのバージョンでも消せない環境がある
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
                try { session.IsBorderRequired = false; } catch (Exception) { }
            session.StartCapture();
        }

        public static CaptureSource? TryCreate(Gpu gpu, nint handle, Func<nint, GraphicsCaptureItem> createItem, bool cursor)
        {
            try { return new(gpu, createItem(handle), cursor); }
            catch (Exception e) when (e is COMException or ArgumentException) { return null; } // 録れないウィンドウ(ツールチップの一部など)は飛ばす
        }

        /// <summary>
        /// 最新のフレームを、合成先(上の行から並んだ BGRA)の (x, y) に描く。まだ1枚も届いていなければ何もしない。
        /// blend なら透明度を見て重ねる(そのまま写すと、Windows 11 のウィンドウの角の外が黒い四角になる)。
        /// </summary>
        public void DrawTo(byte[] canvas, int cw, int ch, int x, int y, bool blend)
        {
            Update();
            var from = Math.Max(0, -x);
            var count = Math.Min(w, cw - x) - from;
            if (count <= 0) return;
            for (var row = Math.Max(0, -y); row < h && y + row < ch; row++)
            {
                var src = pixels.AsSpan((row * w + from) * 4, count * 4);
                var dst = canvas.AsSpan(((y + row) * cw + x + from) * 4, count * 4);
                if (blend) Alpha.Over(src, dst);
                else src.CopyTo(dst);
            }
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

    // IGraphicsCaptureItemInterop の vtable: 3 が CreateForWindow、4 が CreateForMonitor
    static GraphicsCaptureItem CreateWindowItem(nint hwnd) => CreateItem(hwnd, 3);

    static GraphicsCaptureItem CreateMonitorItem(nint monitor)
    {
        try { return CreateItem(monitor, 4); }
        // ウィンドウと違い、飛ばすと真っ白な動画になるだけなので失敗にする
        catch (Exception e) when (e is COMException or ArgumentException) { throw new InvalidOperationException("モニターを取り込めない", e); }
    }

    static unsafe GraphicsCaptureItem CreateItem(nint handle, int slot)
    {
        using var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = QueryInterface(factory.ThisPtr, CaptureItemInteropIid);
        try
        {
            var iid = GraphicsCaptureItemIid;
            nint ptr;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(*(nint**)interop + slot)))(interop, handle, &iid, &ptr));
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
