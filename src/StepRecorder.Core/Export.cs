using System.Globalization;
using System.Net;
using System.Text;

namespace StepRecorder.Core;

/// <summary>出力の1セクション。Sequence ではステップ1つ、ByScreen では同じ画面のステップ群。</summary>
public sealed record Section(string Title, IReadOnlyList<Step> Steps)
{
    /// <summary>全ステップの説明文の行。</summary>
    public IReadOnlyList<string> Lines => Steps.SelectMany(Export.LinesOf).ToList();
}

/// <summary>画面一覧モードで「同じ画面」とみなす単位。</summary>
public sealed record Screen(string ProcessName, string ClassName, string Name);

public static class Export
{
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

    public static string Title(Session s) => $"{s.TargetName} 操作記録 ({s.CreatedAt:yyyy-MM-dd})";

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
            .clip-wrap { position: relative; width: fit-content; max-width: 100%; }
            .keys { position: absolute; left: 0; right: 0; bottom: max({{Number(KeyOverlay.Bottom)}}em, 56px); /* 一時停止中などに出るプレーヤーの操作バーと重ねない(焼き込みには操作バーが無いので比率どおり) */ display: flex; flex-direction: column; align-items: center; gap: {{Number(KeyOverlay.Gap)}}em; pointer-events: none; font-weight: bold; }
            .keys span { background: rgba(32,32,32,.85); color: #fff; border-radius: {{Number(KeyOverlay.Radius)}}em; padding: {{Number(KeyOverlay.PadY)}}em {{Number(KeyOverlay.PadX)}}em; white-space: pre; max-width: calc(100% - 2em); overflow: hidden; text-overflow: ellipsis; }
            .clip-wrap canvas.mouse { position: absolute; pointer-events: none; }
            label.rate { display: block; width: fit-content; margin: .4em 0 0; color: #555; }
            label.rate select { font: inherit; }
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
                if (st.VideoFile != null) AppendClip(sb, s, st);
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
              // 速さの選択。最初はステップに指定された速さ。defaultPlaybackRate にも入れないと、読み込みで等速に戻る
              const rate = (v.closest('.clip-wrap') ?? v).nextElementSibling.querySelector('select'); // キーを重ねる動画は枠で包んでいる
              const applyRate = () => { v.defaultPlaybackRate = v.playbackRate = +rate.value; };
              applyRate();
              rate.addEventListener('change', applyRate);
              // プレーヤー自体のメニューで変えられたら、選択の表示を合わせる(選択肢に無い速さなら足す)
              v.addEventListener('ratechange', () => {
                const now = String(v.playbackRate);
                if (![...rate.options].some(o => o.value === now)) rate.add(new Option(`${now} 倍速`, now));
                rate.value = now;
              });
              // ブラウザが区間の終わりに気づくのは 0.1 秒ほど遅く、8 倍速だと区間の外が 0.7 秒分見えてしまう。描画ごとに見て止める
              const stopAtEnd = () => {
                if (v.paused) return;
                if (v.currentTime >= end) v.pause(); else requestAnimationFrame(stopAtEnd);
              };
              v.addEventListener('play', () => { if (v.currentTime < end) requestAnimationFrame(stopAtEnd); });
            });
            // 録画中に押したキーを、再生に合わせて重ねる(配布用の書き出しでは動画に焼き込むので出さない)。
            // data-keys は [出し始め, 消す, 表示] の秒(動画ファイルの時刻)。大きさは動画の高さに比例させる(焼き込みと同じ比率)
            document.querySelectorAll('video[data-keys]').forEach(v => {
              const chips = JSON.parse(v.dataset.keys), box = v.parentElement.querySelector('.keys');
              const size = +v.dataset.keySize, rows = +v.dataset.keyRows;
              let shown = null;
              const draw = () => {
                box.style.fontSize = `${size * v.clientHeight / rows}px`;
                const t = v.currentTime, now = chips.filter(c => c[0] <= t && t < c[1]).slice(-MAX_KEYS).map(c => c[2]);
                if (shown !== null && now.join('\n') === shown) return;
                shown = now.join('\n');
                box.replaceChildren(...now.map(text => Object.assign(document.createElement('span'), { textContent: text })));
              };
              const loop = () => { draw(); if (!v.paused) requestAnimationFrame(loop); };
              v.addEventListener('play', () => requestAnimationFrame(loop));
              ['seeked', 'timeupdate', 'loadedmetadata', 'pause'].forEach(e => v.addEventListener(e, draw));
              new ResizeObserver(() => { shown = null; draw(); }).observe(v);
            });
            // クリックとホイールを、再生に合わせて重ねる(焼き込みと同じ見た目。クリックは広がりながら消える輪、ホイールは向きの三角)。
            // data-mouse は [出し始め, 消す, x, y, 操作] で、操作は 0〜2 がボタン(左・右・中)、3〜6 がホイール(上・下・左・右)
            document.querySelectorAll('video[data-mouse]').forEach(v => {
              const marks = JSON.parse(v.dataset.mouse), [ax, ay, , ah] = v.dataset.mouseArea.split(' ').map(Number), radius = +v.dataset.mouseRadius;
              const canvas = v.parentElement.querySelector('canvas.mouse'), ctx = canvas.getContext('2d');
              const colors = ['244,67,54', '33,150,243', '76,175,80'];
              const draw = () => {
                const dpr = devicePixelRatio, [w, h] = [v.clientWidth, v.clientHeight], k = h / ah, r = radius * k;
                Object.assign(canvas.style, { left: `${v.clientLeft}px`, top: `${v.clientTop}px`, width: `${w}px`, height: `${h}px` });
                if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr)) [canvas.width, canvas.height] = [Math.round(w * dpr), Math.round(h * dpr)];
                ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
                ctx.clearRect(0, 0, w, h);
                const t = v.currentTime;
                for (const [s, e, x, y, a] of marks) {
                  if (t < s || t >= e) continue;
                  const cx = (x - ax) * k, cy = (y - ay) * k;
                  ctx.beginPath();
                  if (a < 3) {
                    const p = (Math.min(PHASES - 1, Math.floor((t - s) / (e - s) * PHASES)) + 0.5) / PHASES, fade = 1 - p * 0.8;
                    ctx.arc(cx, cy, r * (0.35 + 0.65 * p), 0, Math.PI * 2);
                    ctx.fillStyle = `rgba(${colors[a]},${0.35 * fade})`;
                    ctx.strokeStyle = `rgba(${colors[a]},${fade})`;
                    ctx.lineWidth = r * 0.14;
                    ctx.fill();
                    ctx.stroke();
                    // 押したボタンを塗ったマウスの絵。右下に置くとはみ出すなら左・上に置く(MouseOverlay.PicSide)
                    const pw = r * PIC_W, ph = r * PIC_H, off = r * PIC_OFF;
                    const px = cx + off + pw > w ? cx - off - pw : cx + off, py = cy + off + ph > h ? cy - off - ph : cy + off, split = py + ph * 0.42;
                    const body = new Path2D();
                    body.roundRect(px, py, pw, ph, pw * 0.45);
                    ctx.fillStyle = '#fff';
                    ctx.fill(body);
                    ctx.fillStyle = `rgb(${colors[a]})`;
                    if (a === 2) { ctx.beginPath(); ctx.roundRect(px + pw * 0.36, py + ph * 0.08, pw * 0.28, ph * 0.28, pw * 0.14); ctx.fill(); }
                    else { ctx.save(); ctx.clip(body); ctx.fillRect(a === 0 ? px : px + pw / 2, py, pw / 2, split - py); ctx.restore(); }
                    ctx.strokeStyle = '#303030';
                    ctx.lineWidth = Math.max(1, r * 0.07);
                    ctx.stroke(body);
                    ctx.beginPath();
                    ctx.moveTo(px, split); ctx.lineTo(px + pw, split);
                    if (a !== 2) { ctx.moveTo(px + pw / 2, py); ctx.lineTo(px + pw / 2, split); }
                    ctx.stroke();
                  } else {
                    ctx.arc(cx, cy, r * 0.75, 0, Math.PI * 2);
                    ctx.fillStyle = 'rgba(32,32,32,.85)';
                    ctx.fill();
                    const [dx, dy] = [[0, -1], [0, 1], [-1, 0], [1, 0]][a - 3], q = r * 0.4;
                    ctx.beginPath();
                    ctx.moveTo(cx + dx * q, cy + dy * q);
                    ctx.lineTo(cx - dx * q * 0.6 - dy * q, cy - dy * q * 0.6 + dx * q);
                    ctx.lineTo(cx - dx * q * 0.6 + dy * q, cy - dy * q * 0.6 - dx * q);
                    ctx.closePath();
                    ctx.fillStyle = '#fff';
                    ctx.fill();
                  }
                }
              };
              const loop = () => { draw(); if (!v.paused) requestAnimationFrame(loop); };
              v.addEventListener('play', () => requestAnimationFrame(loop));
              ['seeked', 'timeupdate', 'loadedmetadata', 'pause'].forEach(e => v.addEventListener(e, draw));
              new ResizeObserver(draw).observe(v);
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

            """.Replace("MAX_KEYS", KeyOverlay.MaxVisible.ToString(CultureInfo.InvariantCulture))
               .Replace("PHASES", MouseOverlay.Phases.ToString(CultureInfo.InvariantCulture))
               .Replace("PIC_W", Number(MouseOverlay.PicWidth)).Replace("PIC_H", Number(MouseOverlay.PicHeight)).Replace("PIC_OFF", Number(MouseOverlay.PicOffset)));
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
    static void AppendClip(StringBuilder sb, Session s, Step st)
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
        var range = (st.VideoStart is { } a ? $" data-start=\"{Seconds(a)}\"" : "") + (st.VideoEnd is { } b ? $" data-end=\"{Seconds(b)}\"" : "");
        var chips = KeyOverlay.Chips(KeyOverlay.StrokesFor(s, st));
        var keys = chips.Count == 0 ? "" :
            $" data-keys=\"{H(Json(chips.Select(k => new object[] { Sec(k.Start), Sec(k.End), k.Text })))}\" data-key-size=\"{Number(KeyOverlay.FontSize(c.Height))}\" data-key-rows=\"{c.Height}\"";
        // マウスは [出し始め, 消す, x, y, 操作] の秒と、トリミング前の動画上の位置。data-mouse-area はトリミングの範囲(見えている所)
        var marks = MouseOverlay.Marks(MouseOverlay.StrokesFor(s, st));
        var mouse = marks.Count == 0 ? "" :
            $" data-mouse=\"{H(Json(marks.Select(m => new object[] { Sec(m.Start), Sec(m.End), m.X, m.Y, (int)m.Action })))}\" data-mouse-area=\"{c.X} {c.Y} {c.Width} {c.Height}\" data-mouse-radius=\"{Number(MouseOverlay.Radius(c.Height))}\"";
        var wrap = chips.Count > 0 || marks.Count > 0;
        if (wrap) sb.Append("<div class=\"clip-wrap\">");
        sb.Append($"<video class=\"clip\" controls preload=\"metadata\" src=\"{H(ClipSource(st))}\" poster=\"{H(st.ImageFile)}\" width=\"{c.Width}\"{range}{keys}{mouse}{style}></video>");
        if (marks.Count > 0) sb.Append("<canvas class=\"mouse\"></canvas>");
        if (chips.Count > 0) sb.Append("<div class=\"keys\"></div>");
        sb.Append(wrap ? "</div>\n" : "\n");
        AppendRate(sb, st.Speed);
    }

    /// <summary>動画の速さの選択。最初に選ばれているのがステップに指定された速さ。</summary>
    static void AppendRate(StringBuilder sb, double speed)
    {
        static string Value(double v) => v.ToString(CultureInfo.InvariantCulture);
        // 手で直した session.json の、選択肢に無い速さも出す
        var choices = PlaybackSpeed.Choices.Append(speed).Distinct().Order();
        sb.Append("<label class=\"rate\">再生の速さ <select autocomplete=\"off\">");
        foreach (var v in choices)
            sb.Append($"<option value=\"{Value(v)}\"{(v == speed ? " selected" : "")}>{PlaybackSpeed.Label(v)}</option>");
        sb.Append("</select></label>\n");
    }

    /// <summary>動画のステップの参照先。区間があればメディアフラグメントを付ける。</summary>
    public static string ClipSource(Step st) => (st.VideoStart, st.VideoEnd) switch
    {
        (null, null) => st.VideoFile!,
        (var a, null) => $"{st.VideoFile}#t={Seconds(a!.Value)}",
        (var a, var b) => $"{st.VideoFile}#t={Seconds(a ?? TimeSpan.Zero)},{Seconds(b!.Value)}",
    };

    static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    static string Number(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    static double Sec(TimeSpan t) => Math.Round(t.TotalSeconds, 3);

    /// <summary>HTML のスクリプトに渡すキー・マウスの表示。</summary>
    static string Json(IEnumerable<object[]> rows) =>
        System.Text.Json.JsonSerializer.Serialize(rows,
            new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }); // 属性に入れるときに H() で HTML エスケープする

    /// <summary>session.json と index.html を書き出す。</summary>
    public static void SaveAll(Session s, string dir, GroupMode mode)
    {
        SessionStore.Save(s, dir);
        File.WriteAllText(Path.Combine(dir, HtmlFile), ToHtml(s, mode));
    }

    /// <summary>
    /// 配布用に、outDir へ index.html と、使っている画像・動画だけを書き出す。
    /// session.json と全編の動画は含めない(あとから編集はできない)。
    /// 動画のステップは makeClip(ステップ, 動画の書き出し先, ポスター画像の書き出し先) で、区間・トリミング・キーの表示(GIF なら速さも)を反映した
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
                (st.VideoFile, st.ImageFile, st.VideoStart, st.VideoEnd, st.Crop) = (clip, poster, null, null, null);
                (st.Keys, st.Mouse) = (null, null); // キーとマウスは動画に焼き込み済み。HTML では重ねない
                if (gif) st.VideoSpeed = null; // GIF はプレーヤーが無いので焼き込み済み。mp4 は等速のままで、HTML のプレーヤーが変える
                continue;
            }
            var to = Path.Combine(outDir, st.ImageFile);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(Path.Combine(srcDir, st.ImageFile), to, overwrite: true);
        }
        File.WriteAllText(Path.Combine(outDir, HtmlFile), ToHtml(copy, mode));
    }
}
