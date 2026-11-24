namespace StepRecorder.Core;

/// <summary>マウスの操作。Back・Forward はマウスの横の戻る・進むボタン(XBUTTON1・2)。WheelUp 以降がホイール。</summary>
public enum MouseAction { Left, Right, Middle, Back, Forward, WheelUp, WheelDown, WheelLeft, WheelRight }

/// <summary>動画上の時刻と位置(画素。トリミング前)。</summary>
public sealed record MousePoint(TimeSpan Time, int X, int Y);

/// <summary>マウスの操作 1 つ。Time は動画上の時刻、(X, Y) は動画上の画素の位置(トリミング前)。</summary>
public sealed record MouseStroke(TimeSpan Time, int X, int Y, MouseAction Action)
{
    /// <summary>ボタンを押したまま動かしたときの、離すまでの位置(最後は離した時刻)。動かさずに離したら null。</summary>
    public List<MousePoint>? Drag { get; set; }
}

/// <summary>画面に出すマウスの表示 1 つ。Start から End まで出す。輪は (X, Y)、マウスの絵は Drag に沿って動く。</summary>
public sealed record MouseMark(TimeSpan Start, TimeSpan End, int X, int Y, MouseAction Action, IReadOnlyList<MousePoint>? Drag = null)
{
    public bool IsWheel => MouseOverlay.IsWheel(Action);

    /// <summary>time にマウスの絵を置く位置(押した所か、ドラッグ中ならその時刻までに動いた所)。</summary>
    public (int X, int Y) PicAt(TimeSpan time) =>
        Drag?.LastOrDefault(p => p.Time <= time) is { } p ? (p.X, p.Y) : (X, Y);
}

/// <summary>time に出す表示 1 つ。Phase はクリックの輪の広がりの段階(0〜Phases-1。広がり終えたら null、ホイールは 0)。(PicX, PicY) はマウスの絵の位置。</summary>
public sealed record MouseShown(MouseMark Mark, int? Phase, int PicX, int PicY);

/// <summary>
/// 動画に重ねるマウスの表示の時間割。クリックは広がって消える輪と、押したボタンを塗ったマウスの絵(ボタンで色も違う。戻る・進むは胴に矢印)。ホイールは向きの矢印。
/// 焼き込み・編集画面・HTML はどれもこれを描くだけにして、見た目を揃える。
/// </summary>
public static class MouseOverlay
{
    /// <summary>クリックの輪が広がって消えるまで。</summary>
    public static readonly TimeSpan Ripple = TimeSpan.FromSeconds(0.6);
    /// <summary>最後にホイールを回してから矢印を消すまで。</summary>
    public static readonly TimeSpan WheelLinger = TimeSpan.FromSeconds(0.6);
    /// <summary>これより間が空かなければ、同じ向きのホイールは 1 つの矢印にまとめる。</summary>
    public static readonly TimeSpan WheelGap = TimeSpan.FromSeconds(0.4);
    /// <summary>輪の広がり方の段階の数(焼き込みは段階ごとに 1 度だけ描く)。</summary>
    public const int Phases = 6;

    public static bool IsWheel(MouseAction action) => action >= MouseAction.WheelUp;

    /// <summary>高さ height(画素)の動画に出す輪の半径(画素)。キーの文字の大きさに合わせる。</summary>
    public static double Radius(double height) => KeyOverlay.FontSize(height) * 0.8;

    // クリックに添えるマウスの絵。半径に対する比率で、押した所から右下へ PicOffset 離して置く(焼き込み・編集画面・HTML で同じ値を使う)
    public const double PicOffset = 0.55, PicWidth = 0.95, PicHeight = 1.35;

    /// <summary>
    /// (x, y) のクリックに添えるマウスの絵を、右下に置くと見えている範囲(w×h)からはみ出すなら、左・上に置く。
    /// 閉じるボタンなど、画面の端のクリックでも絵が切れないように。
    /// </summary>
    public static (bool Left, bool Up) PicSide(double x, double y, double r, double w, double h) =>
        (x + r * (PicOffset + PicWidth) > w, y + r * (PicOffset + PicHeight) > h);

    /// <summary>出さない設定かどうかに関わらず、そのステップで記録したマウスの操作(Recorded)。</summary>
    public static IReadOnlyList<MouseStroke> StrokesFor(Session session, Step step) => step.HideMouse ? [] : Recorded(session, step);

    /// <summary>動画のステップでのマウスの操作(そのステップの動画ファイルの時刻)。区間の扱いはキーと同じ。</summary>
    public static IReadOnlyList<MouseStroke> Recorded(Session session, Step step) =>
        KeyOverlay.InStep(session, step, session.Mouse, step.Mouse, m => m.Time);

    public static IReadOnlyList<MouseMark> Marks(IReadOnlyList<MouseStroke> strokes)
    {
        var marks = new List<(MouseStroke First, TimeSpan Last)>();
        foreach (var m in strokes.OrderBy(m => m.Time))
        {
            var wheel = IsWheel(m.Action);
            var at = marks.FindLastIndex(x => x.First.Action == m.Action);
            if (wheel && at >= 0 && m.Time - marks[at].Last <= WheelGap) marks[at] = marks[at] with { Last = m.Time };
            else marks.Add((m, m.Time));
        }
        return marks.Select(x => new MouseMark(x.First.Time, End(x.First, x.Last), x.First.X, x.First.Y, x.First.Action, x.First.Drag)).ToList();
    }

    // ホイールは最後に回してから少し残す。クリックは輪が消えるまでで、ドラッグなら離すまでマウスの絵を出す
    static TimeSpan End(MouseStroke m, TimeSpan last) =>
        IsWheel(m.Action) ? last + WheelLinger
        : m.Drag is [.., var up] && up.Time > m.Time + Ripple ? up.Time : m.Time + Ripple;

    /// <summary>time に出す表示。</summary>
    public static IReadOnlyList<MouseShown> Visible(IReadOnlyList<MouseMark> marks, TimeSpan time) =>
        marks.Where(m => m.Start <= time && time < m.End)
            .Select(m =>
            {
                var (x, y) = m.PicAt(time);
                var phase = (int)((time - m.Start) / Ripple * Phases);
                return new MouseShown(m, m.IsWheel ? 0 : phase < Phases ? phase : null, x, y);
            })
            .ToList();
}
