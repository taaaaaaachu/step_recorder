namespace StepRecorder.ColorDiag;

/// <summary>どの段階の検査か。</summary>
/// <remarks>GpuDefault は色空間を指定しない GPU の変換。正常な PC でもドライバーごとに違うので、それだけでは不良としない。</remarks>
enum Stage { OldRoute, WindowsConverter, GpuConverter, GpuDefault, GpuReadBack, Encoder, ReadBack, CaptureStill, CaptureVideo }

/// <summary>Ok が null なら実行できなかった。</summary>
sealed record Check(Stage Stage, string Name, bool? Ok, string Detail);

/// <summary>測った値から、正常か・どう壊れているかを決める。</summary>
static class Judge
{
    public static readonly int[] Levels = [0, 16, 64, 128, 180, 200, 220, 230, 235, 240, 245, 248, 250, 252, 255];

    const double Tolerance = 3.5;

    static readonly (string Name, Func<double, double> Model)[] YuvModels =
    [
        ("正常(16〜235)", l => 16 + l * 219 / 255),
        ("縮めずに 16 を足している(239 以上が白潰れ)", l => Math.Min(255, 16 + l)),
        ("範囲が 0〜255 のまま(範囲の取り違え)", l => l),
        ("入力を 16〜235 とみなして広げている(明暗の両端が潰れる)", l => 16 + Math.Clamp((l - 16) * 255 / 219, 0, 255) * 219 / 255),
    ];

    static readonly (string Name, Func<double, double> Model)[] RgbModels =
    [
        ("正常", l => l),
        ("明るい側へ 255/219 倍している(白潰れ)", l => Math.Min(255, l * 255 / 219)),
        ("二重に広げている(明暗の両端が潰れる)", l => Math.Clamp((l - 16) * 255 / 219, 0, 255)),
        ("16〜235 のまま広げていない(眠い色)", l => 16 + l * 219 / 255),
    ];

    /// <summary>Levels のグレーを YUV にしたときの Y を判定する。</summary>
    public static (bool Ok, string What) Yuv(IReadOnlyList<int> y) => Match(y, YuvModels, white: 235);

    /// <summary>Levels のグレーを RGB で受け取ったときの値を判定する。</summary>
    public static (bool Ok, string What) Rgb(IReadOnlyList<int> v) => Match(v, RgbModels, white: 255);

    static (bool, string) Match(IReadOnlyList<int> got, (string Name, Func<double, double> Model)[] models, int white)
    {
        if (got.Count != Levels.Length) throw new ArgumentException("段階の数が合わない");
        var best = models.Select((m, i) => (Index: i, m.Name, Error: Levels.Select((l, j) => Math.Abs(got[j] - m.Model(l))).Max())).MinBy(m => m.Error);
        if (best.Error <= Tolerance) return (best.Index == 0, best.Name);
        // どの式にも合わないが、上の方の段階が同じ白になっている(HDR の補正漏れなど)
        var clipped = Levels.Where((_, i) => Math.Abs(got[i] - white) <= 2).ToList();
        return clipped.Any(l => l < 246) ? (false, $"明るさ {clipped.Min()} 以上が同じ白に潰れている") : (false, "既知のどの式とも合わない");
    }

    /// <summary>録画済みの mp4 が白潰れの不良か。正常なら Y は 235 までで、圧縮の誤差がはみ出すだけ。</summary>
    public static (bool Ok, string What) File(double over240Ratio) => over240Ratio > 0.01
        ? (false, $"Y が 240 を超える画素が {over240Ratio:P1}。変換が壊れた状態で作られた動画")
        : (true, "Y は正常な範囲(16〜235)に収まっている");

    /// <summary>全部の検査から、結論を文章にする。</summary>
    public static List<string> Conclude(IReadOnlyList<Check> checks)
    {
        bool Bad(Stage s) => checks.Any(c => c.Stage == s && c.Ok == false);
        var result = new List<string>();
        if (Bad(Stage.OldRoute))
        {
            // 修正前の経路と同じ壊れ方をした部品が犯人
            var broken = checks.Where(c => c.Stage == Stage.OldRoute && c.Ok == false).Select(c => c.Detail).ToHashSet();
            var same = checks.Where(c => c.Stage is Stage.WindowsConverter or Stage.GpuConverter or Stage.GpuDefault
                && c.Ok == false && broken.Contains(c.Detail)).ToList();
            var gpu = same.Any(c => c.Stage is Stage.GpuConverter or Stage.GpuDefault);
            var windows = same.Any(c => c.Stage == Stage.WindowsConverter);
            var cause = gpu && windows ? "Windows の変換部品と GPU ドライバーの両方。共通して使っている GPU ドライバーの疑いが強い"
                : gpu ? "GPU ドライバーの変換(Video Processor)。ドライバーの更新・クリーンインストールと、ビデオの色設定の初期化を試す"
                : windows ? "Windows の変換部品。「変換部品」の欄で DLL が Microsoft 製か確かめ、Windows Update と sfc /scannow を試す"
                : "単体では再現しない。変換部品の選ばれ方か、組み合わせの問題。「読み込まれた DLL」の欄を調べる";
            result.Add($"[原因] Windows に任せた RGB→YUV 変換が壊れている。壊れている所: {cause}");
            foreach (var c in same) result.Add($"  同じ壊れ方をした部品: {c.Name}");
            result.Add(Bad(Stage.Encoder)
                ? "[対策] 自前変換でも直らない。エンコーダー自体も壊れている"
                : "[対策] step_recorder は自前変換(修正版)で避けられる");
        }
        else if (Bad(Stage.WindowsConverter) || Bad(Stage.GpuConverter))
            result.Add("[注意] 変換部品の一部が壊れているが、録画で使われる経路では再現しなかった。設定によっては白潰れする");

        if (!Bad(Stage.OldRoute) && Bad(Stage.Encoder)) result.Add("[原因] H.264 エンコーダーが明るさを変えている");
        if (Bad(Stage.ReadBack))
            result.Add("[注意] mp4 からフレームを取り出す処理(YUV→RGB)が壊れている。ポスター、フレームのステップ、書き出し(GIF・切り出し)に影響する");
        if (Bad(Stage.GpuReadBack)) result.Add("[注意] GPU ドライバーの YUV→RGB 変換が壊れている。再生時の見え方にも影響しうる");
        if (Bad(Stage.CaptureStill)) result.Add("[原因] 静止画の取り込みの時点で色が変わっている");
        // 録画の検査はエンコーダーも通るので、エンコーダーが正常なときだけ取り込みのせいと言える
        if (Bad(Stage.CaptureVideo) && !Bad(Stage.Encoder))
            result.Add("[原因] 画面の取り込み(Windows.Graphics.Capture)の時点で色が変わっている。HDR・アプリの色の自動管理・GPU の色設定を確かめる");

        if (result.Count == 0) result.Add("[正常] この PC では白潰れを再現しなかった");
        var skipped = checks.Count(c => c.Ok == null);
        if (skipped > 0) result.Add($"[補足] 実行できなかった検査が {skipped} 件ある(この PC に無い機能ならそのままでよい)");
        return result;
    }
}
