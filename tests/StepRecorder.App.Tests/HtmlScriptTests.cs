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
            TargetName = "Demo",
            Videos = [new() { File = "video.mp4", Keys = [new(TimeSpan.FromSeconds(1), "Ctrl+S", false), new(TimeSpan.FromSeconds(1.2), "a", true), new(TimeSpan.FromSeconds(1.3), "b", true)] }],
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
    public void クリックは再生の時刻に合わせて_トリミングした範囲の位置に描かれる()
    {
        var s = new Session { TargetName = "Demo", Videos = [new() { File = "video.mp4", Mouse = [new(TimeSpan.FromSeconds(1), 300, 200, MouseAction.Left)] }] };
        s.Steps.Add(new Step { Id = 1, ImageFile = "images/0001.png", ImageWidth = 640, ImageHeight = 360, VideoFile = "video.mp4", Crop = new(100, 50, 400, 300) });

        // 描いた所(輪の中心)と、離れた所の不透明さ
        var title = Run(Export.ToHtml(s, GroupMode.Sequence), """
            const v = document.querySelector('video'), c = v.parentElement.querySelector('canvas.mouse');
            const at = t => {
              v.currentTime = t; v.dispatchEvent(new Event('timeupdate'));
              const k = v.clientHeight / 300 * devicePixelRatio, px = (x, y) => c.getContext('2d').getImageData(Math.round(x * k), Math.round(y * k), 1, 1).data[3];
              return `${px(200, 150) > 0 ? 'on' : 'off'},${px(20, 20) > 0 ? 'on' : 'off'}`;
            };
            document.title = [at(0.5), at(1.3), at(2)].join(' / ') + ' ' + (c.style.width === `${v.clientWidth}px`);
            """);

        Assert.Equal("off,off / on,off / off,off true", title);
    }

    [DesktopFact]
    public void ドラッグ中のマウスの絵は動いた所に描かれる()
    {
        var s = new Session { TargetName = "Demo", Videos = [new() { File = "video.mp4" }] };
        s.Videos[0].Mouse = [new(TimeSpan.FromSeconds(1), 100, 100, MouseAction.Left) { Drag = [new(TimeSpan.FromSeconds(1.2), 400, 250), new(TimeSpan.FromSeconds(2), 400, 250)] }];
        s.Steps.Add(new Step { Id = 1, ImageFile = "images/0001.png", ImageWidth = 640, ImageHeight = 360, VideoFile = "video.mp4", Crop = new(0, 0, 640, 360) });
        var r = MouseOverlay.Radius(360);
        var (dx, dy) = (r * (MouseOverlay.PicOffset + MouseOverlay.PicWidth * 0.5), r * (MouseOverlay.PicOffset + MouseOverlay.PicHeight * 0.7)); // 絵の胴の中ほど
        string N(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // 押した所と動いた所の、絵の胴が白いか
        var title = Run(Export.ToHtml(s, GroupMode.Sequence), $$"""
            const v = document.querySelector('video'), c = v.parentElement.querySelector('canvas.mouse');
            const at = t => {
              v.currentTime = t; v.dispatchEvent(new Event('timeupdate'));
              const k = v.clientHeight / 360 * devicePixelRatio, white = (x, y) => [...c.getContext('2d').getImageData(Math.round((x + {{N(dx)}}) * k), Math.round((y + {{N(dy)}}) * k), 1, 1).data].slice(0, 3).every(n => n > 200);
              return `${white(100, 100) ? 'start' : '-'},${white(400, 250) ? 'moved' : '-'}`;
            };
            document.title = [at(1.1), at(1.8), at(2.1)].join(' / ');
            """);

        Assert.Equal("start,- / -,moved / -,-", title);
    }

    [DesktopFact]
    public void HTML1ファイルに埋め込んだ動画は再生でき_途中へ移れる()
    {
        // 2 秒の白い動画を、HTML 1 ファイルに埋め込んで開く
        var work = Path.Combine(dir, "rec");
        Directory.CreateDirectory(work);
        var px = new byte[320 * 180 * 4];
        Array.Fill(px, (byte)255);
        var nv12 = Nv12.FromBgra(px, 320, 180);
        Task.Run(() => VideoRecorder.Encode(Path.Combine(work, "video.mp4"), 320, 180, Enumerable.Range(0, 30).Select(i => (nv12, TimeSpan.FromSeconds(i / 15.0))))).Wait();
        var s = new Session { TargetName = "Demo", Videos = [new() { File = "video.mp4" }] };
        s.Steps.Add(new Step { Id = 1, ImageFile = "images/0001.png", ImageWidth = 320, ImageHeight = 180, VideoFile = "video.mp4" });
        var html = Path.Combine(dir, "single.html");
        Export.ExportSingleHtml(s, work, html, GroupMode.Sequence, ClipFormat.Mp4, (st, clip, poster) => VideoClips.ExportClip(work, st, ClipFormat.Mp4, clip, poster, [], []));

        const string probe = """
            const v = document.querySelector('video');
            v.addEventListener('error', () => document.title = 'video error ' + v.error.code);
            const done = () => { v.currentTime = 1.5; v.addEventListener('seeked', () => document.title = `${v.duration.toFixed(1)} ${v.currentTime.toFixed(1)}`, { once: true }); };
            if (v.readyState >= 1) done(); else v.addEventListener('loadedmetadata', done, { once: true });
            """;
        // ponytail: 動画の読み込みはヘッドレスの仮想時間では待ってもらえず、ほかのテストと並ぶと間に合わないことがある。
        // 読み込む前に終わった(表題がそのまま)ときだけ、3 回まで開き直す
        var page = File.ReadAllText(html);
        var title = "";
        for (var i = 0; i < 3 && (title == "" || title.StartsWith("Demo")); i++) title = Run(page, probe);

        Assert.Equal("2.0 1.5", title);
    }

    [DesktopFact]
    public void 右クリックはマウスの絵の右ボタンを塗る()
    {
        var s = new Session { TargetName = "Demo", Videos = [new() { File = "video.mp4", Mouse = [new(TimeSpan.FromSeconds(1), 200, 100, MouseAction.Right)] }] };
        // トリミングしていないと、動画ファイルの無いこのテストでは高さが決まらない(実際は読み込んだ動画の大きさ)ので、全体をトリミングにする
        s.Steps.Add(new Step { Id = 1, ImageFile = "images/0001.png", ImageWidth = 640, ImageHeight = 360, VideoFile = "video.mp4", Crop = new(0, 0, 640, 360) });
        var r = MouseOverlay.Radius(360);
        var (top, left, right) = (100 + r * (MouseOverlay.PicOffset + MouseOverlay.PicHeight * 0.25),
            200 + r * (MouseOverlay.PicOffset + MouseOverlay.PicWidth * 0.25), 200 + r * (MouseOverlay.PicOffset + MouseOverlay.PicWidth * 0.75));
        string N(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var title = Run(Export.ToHtml(s, GroupMode.Sequence), $$"""
            const v = document.querySelector('video'), c = v.parentElement.querySelector('canvas.mouse');
            v.currentTime = 1.3; v.dispatchEvent(new Event('timeupdate'));
            const k = v.clientHeight / 360 * devicePixelRatio, rgb = (x, y) => [...c.getContext('2d').getImageData(Math.round(x * k), Math.round(y * k), 1, 1).data].slice(0, 3);
            const kind = ([r, g, b]) => b - r > 60 ? 'blue' : r > 200 && g > 200 && b > 200 ? 'white' : `${r},${g},${b}`;
            document.title = `${kind(rgb({{N(left)}}, {{N(top)}}))} ${kind(rgb({{N(right)}}, {{N(top)}}))}`;
            """);

        Assert.Equal("white blue", title);
    }

    [DesktopFact]
    public void キーの無い動画と静止画だけでもスクリプトは止まらない()
    {
        var s = new Session { TargetName = "Demo", Videos = [new() { File = "video.mp4" }] };
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
