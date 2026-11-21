namespace StepRecorder.Core;

/// <summary>記録中のセッションにステップと操作を積む。</summary>
public sealed class Recording(Session session)
{
    public Session Session { get; } = session;

    public int NextId => Session.Steps.Count == 0 ? 1 : Session.Steps.Max(s => s.Id) + 1;

    public string NextImageFile => $"images/{NextId:D4}.png";

    public string NextVideoFile => $"videos/{NextId:D4}.mp4";

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
        if (trigger == CaptureTrigger.Video) step.Description = $"『{window.Title}』の操作(動画)"; // 操作が足されたらその説明に置き換わる
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
