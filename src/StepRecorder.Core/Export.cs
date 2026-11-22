using System.Globalization;
using System.Net;
using System.Text;

namespace StepRecorder.Core;

/// <summary>出力の1セクション。Sequence ではステップ1つ、ByScreen では同じ画面のステップ群。</summary>
public sealed record Section(string Title, IReadOnlyList<Step> Steps)
{
    /// <summary>代表の画像(最初のステップ)。Markdown はこれだけを載せる。</summary>
    public Step Image => Steps[0];

    /// <summary>全ステップの説明文の行。</summary>
    public IReadOnlyList<string> Lines => Steps.SelectMany(Export.LinesOf).ToList();
}

/// <summary>画面一覧モードで「同じ画面」とみなす単位。</summary>
public sealed record Screen(string ProcessName, string ClassName, string Name);

public static class Export
{
    public const string MarkdownFile = "steps.md";
    public const string HtmlFile = "index.html";

    public static IReadOnlyList<Section> Sections(Session s, GroupMode mode)
    {
        if (mode == GroupMode.Sequence)
            return s.Steps.Select(st => new Section(st.ScreenName, [st])).ToList();

        // 初めて出てきた順を保ったまま、同じ画面をまとめる
        return s.Steps.GroupBy(ScreenOf).Select(g => new Section(g.Key.Name, g.ToList())).ToList();
    }

    public static Screen ScreenOf(Step st) => new(st.Window.ProcessName, st.Window.ClassName, st.ScreenName);

    public static IEnumerable<string> LinesOf(Step st) =>
        st.Description.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public static string Title(Session s) => $"{s.ProcessName} 画面一覧 ({s.CreatedAt:yyyy-MM-dd})";

    /// <summary>冒頭に載せるエビデンス情報の表の行。空の項目は載せない。</summary>
    public static IReadOnlyList<(string Label, string Value)> EvidenceRows(Session s)
    {
        var e = s.Evidence;
        var screen = e.ScalePercent > 0 && e.Screen != "" ? $"{e.Screen}(拡大率 {e.ScalePercent}%)" : e.Screen;
        (string, string)[] rows =
        [
            ("対象アプリ", e.AppFile),
            ("バージョン", e.AppVersion),
            ("記録日時", $"{s.CreatedAt:yyyy-MM-dd HH:mm}"),
            ("PC名", e.MachineName),
            ("OS", e.Os),
            ("画面", screen),
            ("記録ツール", e.RecorderVersion == "" ? "" : $"step_recorder {e.RecorderVersion}"),
        ];
        return rows.Where(r => r.Item2 != "").ToList();
    }

    public static string ToMarkdown(Session s, GroupMode mode)
    {
        var sb = new StringBuilder($"# {Title(s)}\n\n| 項目 | 内容 |\n|---|---|\n");
        foreach (var (label, value) in EvidenceRows(s)) sb.Append($"| {label} | {value.Replace("|", "\\|")} |\n");
        foreach (var (sec, i) in Sections(s, mode).Select((x, i) => (x, i + 1)))
        {
            var st = sec.Image;
            sb.Append($"\n## {i}. {sec.Title}\n\n");
            // GIF は画像としてそのまま、mp4 はポスター画像に動画へのリンクを添える
            if (st.VideoFile?.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) == true) sb.Append($"![{sec.Title}]({st.VideoFile})\n");
            else sb.Append($"![{sec.Title}]({st.ImageFile})\n");
            if (st.VideoFile?.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) == false) sb.Append($"\n[▶ 動画を再生{(st.Speed == 1 ? "" : $"({PlaybackSpeed.Label(st.Speed)})")}]({ClipSource(st)})\n");
            if (sec.Lines.Count > 0) sb.Append('\n');
            foreach (var (line, n) in sec.Lines.Select((x, n) => (x, n + 1)))
                sb.Append($"{n}. {line}\n");
        }
        return sb.ToString();
    }

    public static string ToHtml(Session s, GroupMode mode)
    {
        var sb = new StringBuilder();
        sb.Append($$"""
            <!DOCTYPE html>
            <html lang="ja">
            <head>
            <meta charset="utf-8">
            <title>{{H(Title(s))}}</title>
            <style>
            body { font-family: "Yu Gothic UI", "Meiryo", sans-serif; max-width: 1100px; margin: 2em auto; padding: 0 1em; color: #222; }
            section { margin: 2.5em 0; }
            svg.shot { display: block; max-width: 100%; height: auto; border: 1px solid #ccc; cursor: zoom-in; }
            .clip { display: block; max-width: 100%; height: auto; border: 1px solid #ccc; background: #000; }
            svg.shot rect { fill: none; stroke: #e00; stroke-width: 3; vector-effect: non-scaling-stroke; }
            dialog { padding: 0; border: none; max-width: 98vw; max-height: 98vh; }
            dialog svg.shot { max-width: 98vw; max-height: 96vh; width: auto; cursor: zoom-out; border: none; }
            dialog::backdrop { background: rgba(0,0,0,.7); }
            svg.shot .shade { display: none; fill: rgba(0,0,0,.55); }
            svg.shot.uncropped .shade { display: inline; }
            button.crop-toggle { margin: 0 0 .5em; padding: .25em .9em; font: inherit; cursor: pointer; }
            .pager { margin: 0 0 .5em; }
            .pager button { padding: .25em .9em; font: inherit; cursor: pointer; }
            li[data-page] { color: #888; cursor: pointer; }
            li[data-page].current { color: inherit; font-weight: bold; }
            table.evidence { border-collapse: collapse; margin: 1em 0 2em; }
            table.evidence th, table.evidence td { border: 1px solid #ccc; padding: .3em .8em; text-align: left; }
            table.evidence th { background: #f4f4f4; font-weight: normal; color: #555; }
            </style>
            </head>
            <body>
            <h1>{{H(Title(s))}}</h1>

            """);
        sb.Append("<table class=\"evidence\">\n");
        foreach (var (label, value) in EvidenceRows(s)) sb.Append($"<tr><th>{H(label)}</th><td>{H(value)}</td></tr>\n");
        sb.Append("</table>\n");
        foreach (var (sec, i) in Sections(s, mode).Select((x, i) => (x, i + 1)))
        {
            sb.Append($"<section>\n<h2>{i}. {H(sec.Title)}</h2>\n");
            // 画面一覧モードで同じ画面のステップが複数あるときは、ボタンで前後のステップの画像に切り替える
            var paged = sec.Steps.Count > 1;
            if (paged)
                sb.Append($"<div class=\"pager\"><button type=\"button\" class=\"prev\" disabled>◀ 前へ</button> " +
                    $"<span class=\"pos\">1 / {sec.Steps.Count}</span> <button type=\"button\" class=\"next\">次へ ▶</button></div>\n");
            foreach (var (st, k) in sec.Steps.Select((x, k) => (x, k)))
            {
                if (paged) sb.Append($"<div class=\"page\" data-page=\"{k}\"{(k > 0 ? " hidden" : "")}>\n");
                if (st.VideoFile != null) AppendClip(sb, st);
                else AppendShot(sb, st);
                if (paged) sb.Append("</div>\n");
            }
            if (sec.Lines.Count > 0)
            {
                sb.Append("<ol>\n");
                // ページを切り替えると、そのステップの説明が強調される
                foreach (var (st, k) in sec.Steps.Select((x, k) => (x, k)))
                    foreach (var line in LinesOf(st))
                        sb.Append(paged ? $"<li data-page=\"{k}\"{(k == 0 ? " class=\"current\"" : "")}>{H(line)}</li>\n" : $"<li>{H(line)}</li>\n");
                sb.Append("</ol>\n");
            }
            sb.Append("</section>\n");
        }
        sb.Append("""
            <dialog id="zoom"></dialog>
            <script>
            const zoom = document.getElementById('zoom');
            document.querySelectorAll('section svg.shot').forEach(svg => svg.addEventListener('click', () => {
              zoom.replaceChildren(svg.cloneNode(true));
              zoom.showModal();
            }));
            zoom.addEventListener('click', () => zoom.close());
            // トリミングの解除と、もう一度トリミングする切り替え
            document.querySelectorAll('button.crop-toggle').forEach(btn => btn.addEventListener('click', () => {
              const svg = btn.parentElement.querySelector('svg.shot');
              const full = svg.classList.toggle('uncropped');
              const [x, y, w, h] = (full ? svg.dataset.full : svg.dataset.crop).split(' ');
              svg.setAttribute('viewBox', `${x} ${y} ${w} ${h}`);
              svg.setAttribute('width', w);
              svg.setAttribute('height', h);
              btn.textContent = full ? 'トリミングする' : 'トリミングを解除';
            }));
            // 動画は再生し終えたら区間の最初に戻す。区間(#t=開始,終了)の終わりではブラウザは一時停止するだけで、
            // そのまま ▶ を押すと区間の外まで再生してしまうため
            document.querySelectorAll('video.clip').forEach(v => {
              const start = +(v.dataset.start ?? 0), end = v.dataset.end === undefined ? Infinity : +v.dataset.end;
              const rewind = () => { if (v.ended || v.currentTime >= end - 0.05) v.currentTime = start; };
              v.addEventListener('pause', rewind);
              v.addEventListener('ended', rewind);
              // ステップの速さ。defaultPlaybackRate にも入れないと、読み込みで等速に戻る
              if (v.dataset.speed) v.defaultPlaybackRate = v.playbackRate = +v.dataset.speed;
              // ブラウザが区間の終わりに気づくのは 0.1 秒ほど遅く、8 倍速だと区間の外が 0.7 秒分見えてしまう。描画ごとに見て止める
              const stopAtEnd = () => {
                if (v.paused) return;
                if (v.currentTime >= end) v.pause(); else requestAnimationFrame(stopAtEnd);
              };
              v.addEventListener('play', () => { if (v.currentTime < end) requestAnimationFrame(stopAtEnd); });
            });
            // 画面一覧モードで、同じ画面のステップを前後に切り替える(説明の行をクリックしてもそのステップへ)
            document.querySelectorAll('section').forEach(sec => {
              const pages = sec.querySelectorAll('.page');
              if (!pages.length) return;
              let cur = 0;
              const show = i => {
                cur = i;
                pages.forEach((p, k) => {
                  p.hidden = k !== i;
                  if (k !== i) p.querySelectorAll('video').forEach(v => v.pause());
                });
                sec.querySelectorAll('li[data-page]').forEach(li => li.classList.toggle('current', +li.dataset.page === i));
                sec.querySelector('.pos').textContent = `${i + 1} / ${pages.length}`;
                sec.querySelector('.prev').disabled = i === 0;
                sec.querySelector('.next').disabled = i === pages.length - 1;
              };
              sec.querySelector('.prev').addEventListener('click', () => show(cur - 1));
              sec.querySelector('.next').addEventListener('click', () => show(cur + 1));
              sec.querySelectorAll('li[data-page]').forEach(li => li.addEventListener('click', () => show(+li.dataset.page)));
            });
            </script>
            </body>
            </html>

            """);
        return sb.ToString();
    }


    static string H(string? t) => WebUtility.HtmlEncode(t ?? "");

    /// <summary>静止画。viewBox でトリミングし、強調枠を重ねる。元画像は加工しない。</summary>
    static void AppendShot(StringBuilder sb, Step st)
    {
        var (w, h) = (st.ImageWidth, st.ImageHeight);
        var c = st.Crop ?? new PixelRect(0, 0, w, h);
        // トリミングしているときは、解除して画像全体を見られるボタンを付ける
        if (st.Crop != null) sb.Append("<button type=\"button\" class=\"crop-toggle\">トリミングを解除</button>\n");
        // data-crop / data-full はボタンで切り替える表示範囲
        sb.Append($"<svg class=\"shot\" viewBox=\"{c.X} {c.Y} {c.Width} {c.Height}\" width=\"{c.Width}\" height=\"{c.Height}\"");
        if (st.Crop != null) sb.Append($" data-crop=\"{c.X} {c.Y} {c.Width} {c.Height}\" data-full=\"0 0 {w} {h}\"");
        sb.Append('>');
        sb.Append($"<image href=\"{H(st.ImageFile)}\" width=\"{w}\" height=\"{h}\"/>");
        // トリミングを解除したときだけ見える、トリミング範囲の外を暗くする覆い
        if (st.Crop != null)
            sb.Append($"<path class=\"shade\" fill-rule=\"evenodd\" d=\"M0 0H{w}V{h}H0Z M{c.X} {c.Y}h{c.Width}v{c.Height}h{-c.Width}Z\"/>");
        foreach (var r in st.Highlights)
            sb.Append($"<rect x=\"{r.X}\" y=\"{r.Y}\" width=\"{r.Width}\" height=\"{r.Height}\"/>");
        sb.Append("</svg>\n");
    }

    /// <summary>
    /// 動画。GIF は画像として、mp4 は区間をメディアフラグメント(#t=開始,終了)で指定して再生する。強調枠は付けない。
    /// ponytail: mp4 のトリミングは CSS の object-view-box(Edge・Chrome のみ。Firefox では全体が映る)。
    /// 配布用の書き出しではトリミング済みのファイルを作るので関係ない。
    /// </summary>
    static void AppendClip(StringBuilder sb, Step st)
    {
        var (w, h) = (st.ImageWidth, st.ImageHeight);
        var c = st.Crop ?? new PixelRect(0, 0, w, h);
        if (st.VideoFile!.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append($"<img class=\"clip\" src=\"{H(st.VideoFile)}\" width=\"{w}\" height=\"{h}\" alt=\"\">\n");
            return;
        }
        var style = st.Crop == null ? "" :
            $" style=\"object-view-box: inset({c.Y}px {w - c.X - c.Width}px {h - c.Y - c.Height}px {c.X}px); aspect-ratio: {c.Width} / {c.Height}\"";
        // 区間の開始・終了は、再生し終えたら区間の最初に戻すために持たせる(スクリプトで使う)
        var speed = st.Speed == 1 ? "" : $" data-speed=\"{st.Speed.ToString("0.##", CultureInfo.InvariantCulture)}\"";
        var range = (st.VideoStart is { } a ? $" data-start=\"{Seconds(a)}\"" : "") + (st.VideoEnd is { } b ? $" data-end=\"{Seconds(b)}\"" : "");
        sb.Append($"<video class=\"clip\" controls preload=\"metadata\" src=\"{H(ClipSource(st))}\" poster=\"{H(st.ImageFile)}\" width=\"{c.Width}\"{range}{speed}{style}></video>\n");
    }

    /// <summary>動画のステップの参照先。区間があればメディアフラグメントを付ける。</summary>
    public static string ClipSource(Step st) => (st.VideoStart, st.VideoEnd) switch
    {
        (null, null) => st.VideoFile!,
        (var a, null) => $"{st.VideoFile}#t={Seconds(a!.Value)}",
        (var a, var b) => $"{st.VideoFile}#t={Seconds(a ?? TimeSpan.Zero)},{Seconds(b!.Value)}",
    };

    static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>session.json・steps.md・index.html をまとめて書き出す。</summary>
    public static void SaveAll(Session s, string dir, GroupMode mode)
    {
        SessionStore.Save(s, dir);
        File.WriteAllText(Path.Combine(dir, MarkdownFile), ToMarkdown(s, mode));
        File.WriteAllText(Path.Combine(dir, HtmlFile), ToHtml(s, mode));
    }

    /// <summary>
    /// 配布用に、outDir へ index.html・steps.md と、使っている画像・動画だけを書き出す。
    /// session.json と全編の動画は含めない(あとから編集はできない)。
    /// 動画のステップは makeClip(ステップ, 動画の書き出し先, ポスター画像の書き出し先) で、区間・速さ・トリミングを反映した
    /// ファイルにしてもらう(ポスターは mp4 のときだけ。GIF では null)。
    /// </summary>
    public static void ExportForDistribution(Session s, string srcDir, string outDir, GroupMode mode, ClipFormat format,
        Action<Step, string, string?> makeClip)
    {
        // 書き出し用に、動画のステップを書き出したファイルに差し替えた写しで出力する
        var copy = SessionStore.Clone(s);
        copy.VideoFile = null;
        foreach (var st in Sections(copy, mode).SelectMany(sec => sec.Steps)) // 画面一覧モードの HTML は同じ画面の全ステップを切り替えて見せる
        {
            if (st.VideoFile != null)
            {
                var gif = format == ClipFormat.Gif;
                var clip = $"clips/{st.Id:D4}.{(gif ? "gif" : "mp4")}";
                var poster = $"clips/{st.Id:D4}.png";
                makeClip(s.Steps.First(x => x.Id == st.Id), Path.Combine(outDir, clip), gif ? null : Path.Combine(outDir, poster));
                if (st.Crop is { } c) (st.ImageWidth, st.ImageHeight) = (c.Width, c.Height); // トリミング済みのファイルになる
                (st.VideoFile, st.ImageFile, st.VideoStart, st.VideoEnd, st.VideoSpeed, st.Crop) = (clip, poster, null, null, null, null);
                continue;
            }
            var to = Path.Combine(outDir, st.ImageFile);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(Path.Combine(srcDir, st.ImageFile), to, overwrite: true);
        }
        File.WriteAllText(Path.Combine(outDir, MarkdownFile), ToMarkdown(copy, mode));
        File.WriteAllText(Path.Combine(outDir, HtmlFile), ToHtml(copy, mode));
    }
}
