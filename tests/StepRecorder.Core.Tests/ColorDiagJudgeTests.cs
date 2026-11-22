using StepRecorder.ColorDiag;

namespace StepRecorder.Core.Tests;

/// <summary>診断プログラム(tools/ColorDiag)の判定。</summary>
public class ColorDiagJudgeTests
{
    const string Broken = "縮めずに 16 を足している(239 以上が白潰れ)";

    static int[] Map(Func<double, double> f) => [.. Judge.Levels.Select(l => (int)Math.Round(f(l)))];

    [Fact]
    public void 正常な変換を正常と判定する()
    {
        Assert.Equal((true, "正常(16〜235)"), Judge.Yuv([16, 30, 71, 126, 171, 188, 205, 214, 218, 222, 226, 229, 231, 232, 235])); // 実測値
        Assert.Equal((true, "正常"), Judge.Rgb([0, 16, 64, 128, 180, 200, 220, 231, 235, 240, 244, 248, 250, 251, 255]));
    }

    [Fact]
    public void 白潰れしたPCの壊れ方を見分ける()
    {
        Assert.Equal((false, Broken), Judge.Yuv(Map(l => Math.Min(255, 16 + l))));
        Assert.Equal((false, "範囲が 0〜255 のまま(範囲の取り違え)"), Judge.Yuv(Map(l => l)));
        Assert.Equal((false, "明るい側へ 255/219 倍している(白潰れ)"), Judge.Rgb(Map(l => Math.Min(255, l * 255 / 219))));
        Assert.Equal((false, "16〜235 のまま広げていない(眠い色)"), Judge.Rgb(Map(l => 16 + l * 219 / 255)));
    }

    [Fact]
    public void どの式にも合わない白潰れは潰れ始める明るさを示す() =>
        Assert.Equal((false, "明るさ 180 以上が同じ白に潰れている"), Judge.Rgb(Map(l => Math.Min(255, l * 1.6))));

    [Fact]
    public void 録画済みの動画は範囲外の画素の割合で判定する()
    {
        Assert.False(Judge.File(0.918).Ok); // 白潰れした実物
        Assert.True(Judge.File(0.001).Ok); // 圧縮の誤差
    }

    [Fact]
    public void 正常なPCではGPUの既定の挙動を不良としない()
    {
        var result = Judge.Conclude(
        [
            new(Stage.OldRoute, "旧", true, "正常(16〜235)"),
            new(Stage.GpuDefault, "GPU 未指定", false, "範囲が 0〜255 のまま(範囲の取り違え)"),
            new(Stage.Encoder, "新", true, "正常(16〜235)"),
        ]);
        Assert.Equal(["[正常] この PC では白潰れを再現しなかった"], result);
    }

    [Fact]
    public void 修正前の経路と同じ壊れ方をした部品を原因に挙げる()
    {
        var result = string.Join("\n", Judge.Conclude(
        [
            new(Stage.OldRoute, "旧", false, Broken),
            new(Stage.WindowsConverter, "部品A", true, "正常(16〜235)"),
            new(Stage.GpuDefault, "無関係なGPU", false, "範囲が 0〜255 のまま(範囲の取り違え)"),
            new(Stage.GpuConverter, "犯人のGPU", false, Broken),
            new(Stage.Encoder, "新", true, "正常(16〜235)"),
        ]));
        Assert.Contains("GPU ドライバーの変換", result);
        Assert.Contains("同じ壊れ方をした部品: 犯人のGPU", result);
        Assert.DoesNotContain("無関係なGPU", result);
        Assert.Contains("自前変換(修正版)で避けられる", result);
    }

    [Fact]
    public void 部品単体で再現しなければそう伝える() =>
        Assert.Contains("単体では再現しない", Judge.Conclude([new(Stage.OldRoute, "旧", false, Broken)])[0]);

    [Fact]
    public void エンコーダーが壊れていれば録画の異常を取り込みのせいにしない()
    {
        var result = string.Join("\n", Judge.Conclude([new(Stage.Encoder, "新", false, "x"), new(Stage.CaptureVideo, "録画", false, "x")]));
        Assert.Contains("H.264 エンコーダー", result);
        Assert.DoesNotContain("Windows.Graphics.Capture", result);
    }
}
