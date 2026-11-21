using System.Net;
using System.Text;

namespace StepRecorder.Core;

/// <summary>出力の1セクション。Sequence ではステップ1つ、ByScreen では同じ画面のステップ群。</summary>
public sealed record Section(string Title, Step Image, IReadOnlyList<string> Lines);

public static class Export
{
    public const string MarkdownFile = "steps.md";
    public const string HtmlFile = "index.html";

    public static IReadOnlyList<Section> Sections(Session s, GroupMode mode)
    {
        if (mode == GroupMode.Sequence)
            return s.Steps.Select(st => new Section(st.ScreenName, st, Lines(st).ToList())).ToList();

        // 初めて出てきた順を保ったまま、同じ画面をまとめる
        return s.Steps
            .GroupBy(st => (st.Window.ProcessName, st.Window.ClassName, st.ScreenName))
            .Select(g => new Section(g.Key.ScreenName, g.First(), g.SelectMany(Lines).ToList()))
            .ToList();
    }

    static IEnumerable<string> Lines(Step st) =>
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
            sb.Append($"\n## {i}. {sec.Title}\n\n![{sec.Title}]({sec.Image.ImageFile})\n");
            if (sec.Lines.Count > 0) sb.Append('\n');
            foreach (var (line, n) in sec.Lines.Select((x, n) => (x, n + 1)))
                sb.Append($"{n}. {line}\n");
        }
        return sb.ToString();
    }

    public static string ToHtml(Session s, GroupMode mode)
    {
        static string H(string? t) => WebUtility.HtmlEncode(t ?? "");
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
            svg.shot rect { fill: none; stroke: #e00; stroke-width: 3; vector-effect: non-scaling-stroke; }
            dialog { padding: 0; border: none; max-width: 98vw; max-height: 98vh; }
            dialog svg.shot { max-width: 98vw; max-height: 96vh; width: auto; cursor: zoom-out; border: none; }
            dialog::backdrop { background: rgba(0,0,0,.7); }
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
            var st = sec.Image;
            var c = st.Crop ?? new PixelRect(0, 0, st.ImageWidth, st.ImageHeight);
            sb.Append($"<section>\n<h2>{i}. {H(sec.Title)}</h2>\n");
            // viewBox でトリミングし、強調枠を重ねる。元画像は加工しない
            sb.Append($"<svg class=\"shot\" viewBox=\"{c.X} {c.Y} {c.Width} {c.Height}\" width=\"{c.Width}\" height=\"{c.Height}\">");
            sb.Append($"<image href=\"{H(st.ImageFile)}\" width=\"{st.ImageWidth}\" height=\"{st.ImageHeight}\"/>");
            foreach (var r in st.Highlights)
                sb.Append($"<rect x=\"{r.X}\" y=\"{r.Y}\" width=\"{r.Width}\" height=\"{r.Height}\"/>");
            sb.Append("</svg>\n");
            if (sec.Lines.Count > 0)
            {
                sb.Append("<ol>\n");
                foreach (var line in sec.Lines) sb.Append($"<li>{H(line)}</li>\n");
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
            </script>
            </body>
            </html>

            """);
        return sb.ToString();
    }

    /// <summary>session.json・steps.md・index.html をまとめて書き出す。</summary>
    public static void SaveAll(Session s, string dir, GroupMode mode)
    {
        SessionStore.Save(s, dir);
        File.WriteAllText(Path.Combine(dir, MarkdownFile), ToMarkdown(s, mode));
        File.WriteAllText(Path.Combine(dir, HtmlFile), ToHtml(s, mode));
    }
}
