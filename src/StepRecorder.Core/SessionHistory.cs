using System.Text.Json;

namespace StepRecorder.Core;

/// <summary>編集画面の「元に戻す」「やり直す」用の履歴。変更の前にセッション全体を JSON で控えておく。</summary>
public sealed class SessionHistory(int capacity = 100)
{
    readonly LinkedList<string> undo = new(), redo = new();

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    /// <summary>変更する直前に呼ぶ。新しく変更したので、やり直しの分は捨てる。</summary>
    public void Push(Session s)
    {
        Add(undo, s);
        redo.Clear();
    }

    /// <summary>最後に控えた状態を返す。今の状態 current は、やり直し用に控える。無ければ null。</summary>
    public Session? Undo(Session current) => Move(undo, redo, current);

    /// <summary>最後に元に戻す前の状態を返す。今の状態 current は、元に戻す用に控える。無ければ null。</summary>
    public Session? Redo(Session current) => Move(redo, undo, current);

    Session? Move(LinkedList<string> from, LinkedList<string> to, Session current)
    {
        if (from.Last is not { } last) return null;
        from.RemoveLast();
        Add(to, current);
        return JsonSerializer.Deserialize<Session>(last.Value, SessionStore.Options);
    }

    /// <summary>古いものから capacity を超えた分は捨てる。</summary>
    void Add(LinkedList<string> list, Session s)
    {
        list.AddLast(JsonSerializer.Serialize(s, SessionStore.Options));
        if (list.Count > capacity) list.RemoveFirst();
    }
}
