using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StepRecorder.App;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using Windows.Media.Transcoding;

namespace StepRecorder.ColorDiag;

/// <summary>
/// 録画の白潰れが、取り込み・変換・エンコード・読み戻しのどこで起きるかを調べ、report.txt に書く。
/// 使い方: StepRecorder.ColorDiag.exe [調べたい mp4 ...] [--no-pause]
/// </summary>
static class Program
{
    static readonly List<Check> checks = [];
    static string reportPath = "", work = "";

    [STAThread]
    static void Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { } // 出力先が無くても続ける
        work = CreateOutputDir();
        reportPath = Path.Combine(work, "report.txt");
        Log("step_recorder 動画の色の診断");
        Log("このファイルに PC 名・ユーザー名・画面の内容は含みません。");

        MediaFactory.MFStartup(true).CheckError();
        Section("環境", SystemInfo.Windows);
        Section("GPU とモニター", SystemInfo.GpusAndMonitors);

        // Media Foundation は MTA で動かす
        Task.Run(RunOffscreen).GetAwaiter().GetResult();
        RunCapture();

        Section("変換部品", SystemInfo.Transforms);
        foreach (var video in args.Where(a => !a.StartsWith("--")))
            Section($"渡された動画: {Path.GetFileName(video)}", () =>
            {
                var (ok, what) = Judge.File(Task.Run(() => MediaProbe.Over240Ratio(video)).GetAwaiter().GetResult());
                return [$"{(ok ? "OK" : "NG")} {what}"];
            });

        Log("");
        Log("==== 結論 ====");
        foreach (var line in Judge.Conclude(checks)) Log(line);
        Console.WriteLine();
        Console.WriteLine($"レポート: {reportPath}");
        Console.WriteLine("この report.txt を送ってください。");
        if (!args.Contains("--no-pause") && !Console.IsInputRedirected)
        {
            Console.WriteLine("Enter で閉じます。");
            Console.ReadLine();
        }
    }

    static string CreateOutputDir()
    {
        var name = $"color-diag-{DateTime.Now:yyyyMMdd-HHmmss}";
        try { return Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, name)).FullName; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), name)).FullName; // 書けない場所に置かれた
        }
    }

    /// <summary>1行ずつすぐ書く(ドライバーごと落ちても、そこまでの結果が残る)。</summary>
    static void Log(string line)
    {
        Console.WriteLine(line);
        File.AppendAllText(reportPath, line + Environment.NewLine, Encoding.UTF8);
    }

    static void Section(string title, Func<IEnumerable<string>> lines)
    {
        Log("");
        Log($"==== {title} ====");
        try
        {
            foreach (var line in lines()) Log(line);
        }
        catch (Exception e) { Log($"調べられなかった: {SystemInfo.Anonymize(e.Message)}"); }
    }

    static void Run(Stage stage, string name, Func<Reading> measure)
    {
        File.AppendAllText(reportPath, $"> {name}{Environment.NewLine}", Encoding.UTF8); // 落ちたときに、どれで落ちたか分かるように
        try
        {
            var r = measure();
            var (ok, what) = r.Yuv ? Judge.Yuv(r.Gray) : Judge.Rgb(r.Gray);
            checks.Add(new(stage, name, ok, what));
            Log($"{(ok ? "OK  " : stage == Stage.GpuDefault ? "参考 " : "NG  ")}{name}: {what}");
            Log($"      {(r.Yuv ? "Y  " : "RGB")} {string.Join(",", r.Gray)}  赤緑青 {r.Colors}");
        }
        catch (Exception e)
        {
            var error = e.GetBaseException();
            var message = $"0x{error.HResult:X8} {SystemInfo.Anonymize(error.Message.Trim())}".Trim();
            checks.Add(new(stage, name, null, message));
            Log($"--  {name}: 実行できない({message})");
        }
    }

    static void Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();

    static void RunOffscreen()
    {
        var sizes = new[] { (W: 640, H: 480), (W: 1280, H: 720) }; // BT.601 と BT.709 の両方
        var before = SystemInfo.LoadedModules();

        Log("");
        Log($"==== 検査(グレー {string.Join(",", Judge.Levels)}) ====");
        Log("-- 修正前の経路(変換を Windows に任せる)");
        var n = 0;
        foreach (var (w, h) in sizes)
            foreach (var hardware in new[] { false, true })
                foreach (var algorithm in new[] { MediaVideoProcessingAlgorithm.Default, MediaVideoProcessingAlgorithm.MrfCrf444 })
                {
                    var path = Path.Combine(work, $"old-{++n}.mp4");
                    Run(Stage.OldRoute, $"MediaTranscoder BGRA {w}x{h} HW={hardware} {algorithm}", () =>
                    {
                        Wait(MediaProbe.EncodeBgra(path, w, h, hardware, algorithm));
                        return MediaProbe.ReadChart(path);
                    });
                }
        var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var loaded = SystemInfo.LoadedModules().Except(before, StringComparer.OrdinalIgnoreCase)
            .Where(p => !p.StartsWith(runtime, StringComparison.OrdinalIgnoreCase)).ToList();

        Log("-- Windows の変換部品を単体で");
        foreach (var (w, h) in sizes)
            foreach (var (name, clsid) in new[] { ("Color Converter DSP", MediaProbe.ColorConverter), ("Video Processor MFT", MediaProbe.VideoProcessor) })
                foreach (var tagged in new[] { false, true })
                    Run(Stage.WindowsConverter, $"{name} {w}x{h} 範囲{(tagged ? "指定" : "未指定")}", () => MediaProbe.Convert(clsid, w, h, tagged));

        Log("-- GPU ドライバーの変換を単体で");
        using (var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>())
        {
            var tested = new HashSet<(uint, uint)>();
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
                using (adapter)
                {
                    var desc = adapter.Description1;
                    if ((desc.Flags & AdapterFlags.Software) != 0 || !tested.Add((desc.VendorId, desc.DeviceId))) continue; // 仮想ディスプレイがあると同じ GPU が何個も出る
                    foreach (var (w, h) in sizes)
                        foreach (var toYuv in new[] { true, false })
                            foreach (var explicitSpace in new[] { false, true })
                                Run(!explicitSpace ? Stage.GpuDefault : toYuv ? Stage.GpuConverter : Stage.GpuReadBack,
                                    $"GPU {i} {(toYuv ? "RGB→YUV" : "YUV→RGB")} {w}x{h} 色空間{(explicitSpace ? "指定" : "未指定")}",
                                    () => GpuProbe.Convert(adapter, w, h, toYuv, explicitSpace));
                }
        }

        Log("-- 修正版の経路(自前変換)と、読み戻し");
        foreach (var (w, h) in sizes)
        {
            var path = Path.Combine(work, $"new-{w}x{h}.mp4");
            Run(Stage.Encoder, $"自前変換 + H.264 {w}x{h}", () =>
            {
                var nv12 = Core.Nv12.FromBgra(Chart.Bgra(w, h), w, h);
                Wait(VideoRecorder.Encode(path, w, h, Enumerable.Range(0, 15).Select(i => (nv12, TimeSpan.FromSeconds(i / 15.0)))));
                return MediaProbe.ReadChart(path);
            });
            Run(Stage.ReadBack, $"フレームの取り出し {w}x{h}", () => ReadImage(VideoClips.Frame(path, TimeSpan.FromSeconds(0.4))));
        }

        Section("修正前の経路で読み込まれた DLL", () => SystemInfo.Modules(loaded));
    }

    /// <summary>自分で出した縦縞のウィンドウを、製品と同じ方法で撮る・録る。</summary>
    static void RunCapture()
    {
        Log("");
        Log("==== 画面の取り込み(縦縞のウィンドウが数秒出ます。触らずに待ってください) ====");
        try
        {
            const int w = 720, h = 240;
            var image = new System.Windows.Controls.Image
            {
                Source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, Chart.Bgra(w, h), w * 4),
                Stretch = Stretch.Fill,
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            var window = new Window
            {
                Title = "ColorDiag",
                Width = w,
                Height = h,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Topmost = true,
                Content = image,
            };
            window.Show();
            Pump(TimeSpan.FromSeconds(1));
            var hwnd = new WindowInteropHelper(window).Handle;

            Run(Stage.CaptureStill, "静止画の取り込み(PrintWindow)",
                () => ReadImage((WindowCapture.Capture(hwnd, false) ?? throw new InvalidOperationException("撮れない")).Image));

            var path = Path.Combine(work, "capture.mp4");
            Run(Stage.CaptureVideo, "録画(Windows.Graphics.Capture + 自前変換)", () =>
            {
                var task = Task.Run(() =>
                {
                    var recorder = VideoRecorder.Start(hwnd, path, cursor: false);
                    Thread.Sleep(1500);
                    recorder.Stop();
                    return MediaProbe.ReadChart(path);
                });
                // ウィンドウを描き続けないと録れない
                var deadline = DateTime.Now.AddSeconds(30);
                while (!task.IsCompleted && DateTime.Now < deadline) Pump(TimeSpan.FromMilliseconds(100));
                return task.IsCompleted ? task.GetAwaiter().GetResult() : throw new TimeoutException("録画が終わらない");
            });
            window.Close();
        }
        catch (Exception e) { Log($"調べられなかった: {SystemInfo.Anonymize(e.Message)}"); }
    }

    static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = time };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    static Reading ReadImage(BitmapSource image)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
        var (w, h) = (bgra.PixelWidth, bgra.PixelHeight);
        var bytes = new byte[w * h * 4];
        bgra.CopyPixels(bytes, w * 4, 0);
        return Chart.FromBgra(bytes, w, h, w * 4);
    }
}
