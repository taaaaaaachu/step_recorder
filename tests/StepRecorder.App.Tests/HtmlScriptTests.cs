using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using StepRecorder.Core;

namespace StepRecorder.App.Tests;

/// <summary>
/// 書き出した HTML のスクリプトを Edge(ヘッドレス)で実際に動かす。文字列を見るだけでは、1 つのエラーで後ろの処理が全部止まるのに気づけない。
/// 動画ファイルは無くてよい(読み込む前でも currentTime は設定でき、時刻の通知はスクリプトから出す)。
/// </summary>
public sealed class HtmlScriptTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "sr-html-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    static string? Edge() => new[] { @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", @"C:\Program Files\Microsoft\Edge\Application\msedge.exe" }.FirstOrDefault(File.Exists);

    /// <summary>html に probe(結果を document.title に書くスクリプト)を足して開き、title を返す。</summary>
    string Run(string html, string probe)
    {
        var edge = Edge();
        Assert.True(edge != null, "Edge が無い");
        Directory.CreateDirectory(dir);
        var page = Path.Combine(dir, "probe.html");
        File.WriteAllText(page, html
            .Replace("</head>", "<script>window.addEventListener('error', e => { document.title = 'ERR ' + e.message + ' @' + e.lineno; window.failed = true; });</script></head>")
            .Replace("</body>", $"<script>if (!window.failed) {{ {probe} }}</script></body>"));
        var start = new ProcessStartInfo(edge!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "--headless=new", "--disable-gpu", "--no-first-run", $"--user-data-dir={Path.Combine(dir, "edge")}", "--virtual-time-budget=3000", "--dump-dom", new Uri(page).AbsoluteUri })
            start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        var dom = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync(); // 読まないと詰まる
        Assert.True(p.WaitForExit(30000), "Edge が終わらない");
        return Regex.Match(dom.Result, "<title>(.*?)</title>").Groups[1].Value;
    }

    [DesktopFact]
    public void キーは再生の時刻に合わせて重なり_速さの選択も動く()
    {
        var s = new Session
        {
            TargetName = "Demo", VideoFile = "video.mp4",
            Keys = [new(TimeSpan.FromSeconds(1), "Ctrl+S", false), new(TimeSpan.FromSeconds(1.2), "a", true), new(TimeSpan.FromSeconds(1.3), "b", true)],
        };
        s.Steps.Add(new Step { Id = 1, ImageFile = "images/0001.png", ImageWidth = 640, ImageHeight = 360, VideoFile = "video.mp4", VideoStart = TimeSpan.Zero, VideoEnd = TimeSpan.FromSeconds(5) });

        var title = Run(Export.ToHtml(s, GroupMode.Sequence), """
            const v = document.querySelector('video'), box = v.parentElement.querySelector('.keys');
            const rate = v.parentElement.nextElementSibling.querySelector('select');
            const at = t => { v.currentTime = t; v.dispatchEvent(new Event('timeupdate')); return [...box.children].map(c => c.textContent).join('|') || '-'; };
            const log = [at(0.5), at(1.5), at(9)];
            v.playbackRate = 2; v.dispatchEvent(new Event('ratechange'));
            log.push(rate.value);
            document.title = log.join(' / ');
            """);

        Assert.Equal("- / Ctrl+S|ab / - / 2", title);
    }

    [DesktopFact]
    public void キーの無い動画と静止画だけでもスクリプトは止まらない()
    {
        var s = new Session { TargetName = "Demo", VideoFile = "video.mp4" };
        s.Steps.Add(new Step { Id = 1, ImageFile = "images/0001.png", ImageWidth = 640, ImageHeight = 360, VideoFile = "video.mp4" });
        s.Steps.Add(new Step { Id = 2, ImageFile = "images/0002.png", ImageWidth = 640, ImageHeight = 360 });

        var title = Run(Export.ToHtml(s, GroupMode.Sequence), """
            const v = document.querySelector('video');
            v.playbackRate = 1.5; v.dispatchEvent(new Event('ratechange'));
            document.title = 'ok ' + v.nextElementSibling.querySelector('select').value;
            """);

        Assert.Equal("ok 1.5", title);
    }
}
