namespace StepRecorder.Core;

/// <summary>記録中のセッションにステップと操作を積む。</summary>
public sealed class Recording(Session session)
{
    public Session Session { get; } = session;

    public int NextId => Session.Steps.Count == 0 ? 1 : Session.Steps.Max(s => s.Id) + 1;

    public string NextImageFile => $"images/{NextId:D4}.png";

    public string NextVideoFile => $"videos/{NextId:D4}.mp4";

    /// <summary>
    /// 全編の動画の time から足したステップを入れる位置。動画上の時刻が time 以前の最後のステップの直後
    /// (時刻を持たないステップは見ない)。
    /// </summary>
    public static int IndexByVideoTime(IReadOnlyList<Step> steps, TimeSpan time)
    {
        var i = steps.Count - 1;
        while (i >= 0 && !(steps[i].VideoTime <= time)) i--;
        return i + 1;
    }

    /// <summary>
    /// 動画のステップを at で2つに分ける。st は at までになり、at からの写し(Id は newId)を返す。ファイルは分けず区間だけ変える。
    /// 操作は動画上の時刻で前後に振り分け(時刻の無いものは前)、説明が手で直していないものなら、それぞれ作り直す。
    /// キー・マウスは両方に残す(重ねるときに区間で絞る)。ポスターは呼ぶ側で作り直す。
    /// </summary>
    public static Step SplitVideo(Step st, TimeSpan at, int newId)
    {
        var after = SessionStore.Clone(new Session { Steps = [st] }).Steps[0];
        var edited = st.Description != Describe(st);
        after.Id = newId;
        (st.VideoEnd, after.VideoStart) = (at, at);
        st.Operations.RemoveAll(o => o.VideoTime >= at);
        after.Operations.RemoveAll(o => !(o.VideoTime >= at));
        if (!edited) (st.Description, after.Description) = (Describe(st), Describe(after));
        return after;
    }

    /// <summary>操作から作る説明。動画のステップで操作が無ければ「『画面名』の操作(動画)」。</summary>
    static string Describe(Step st) =>
        st.Operations.Count == 0 && st.Trigger == CaptureTrigger.Video ? VideoDescription(st.Window) : Describer.Describe(st.Operations);

    static string VideoDescription(WindowInfo window) => $"『{window.Title}』の操作(動画)";

    public Step AddStep(CaptureTrigger trigger, WindowInfo window, string imageFile,
        int width, int height, DateTimeOffset time, Operation? op = null)
    {
        var step = new Step
        {
            Id = NextId,
            Time = time,
            Trigger = trigger,
            ImageFile = imageFile,
            ImageWidth = width,
            ImageHeight = height,
            Window = window,
            ScreenName = window.Title,
        };
        if (trigger == CaptureTrigger.Video) step.Description = VideoDescription(window); // 操作が足されたらその説明に置き換わる
        Session.Steps.Add(step);
        if (op != null) Append(step, op);
        return step;
    }

    /// <summary>直近のステップに操作を足す。ステップがまだ無ければ捨てて false。</summary>
    public bool AddOperation(Operation op)
    {
        if (Session.Steps.Count == 0) return false;
        Append(Session.Steps[^1], op);
        return true;
    }

    static void Append(Step step, Operation op)
    {
        var last = step.Operations.LastOrDefault();
        if (op.Kind == OperationKind.TextInput && last?.Kind == OperationKind.TextInput && SameElement(last.Element, op.Element))
            last.Text = op.Text; // 同じ欄への連続入力は1つにまとめる
        else
            step.Operations.Add(op);

        if (op.Element?.Rect is { } r && op.Kind == OperationKind.Click && !step.Highlights.Contains(r))
            step.Highlights.Add(r);
        step.Description = Describer.Describe(step.Operations);
    }

    static bool SameElement(ElementInfo? a, ElementInfo? b) =>
        a != null && b != null && a.Name == b.Name && a.AutomationId == b.AutomationId && a.ControlType == b.ControlType;
}
