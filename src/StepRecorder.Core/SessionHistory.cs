using System.Text.Json;

namespace StepRecorder.Core;

/// <summary>編集画面の「元に戻す」用の履歴。変更の前にセッション全体を JSON で控えておく。</summary>
public sealed class SessionHistory(int capacity = 100)
{
    readonly LinkedList<string> snapshots = new();

    public bool CanUndo => snapshots.Count > 0;

    /// <summary>変更する直前に呼ぶ。古いものから capacity を超えた分は捨てる。</summary>
    public void Push(Session s)
    {
        snapshots.AddLast(JsonSerializer.Serialize(s, SessionStore.Options));
        if (snapshots.Count > capacity) snapshots.RemoveFirst();
    }

    /// <summary>最後に控えた状態を返す(履歴からは取り除く)。無ければ null。</summary>
    public Session? Undo()
    {
        if (snapshots.Last is not { } last) return null;
        snapshots.RemoveLast();
        return JsonSerializer.Deserialize<Session>(last.Value, SessionStore.Options);
    }
}
