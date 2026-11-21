namespace StepRecorder.Core;

/// <summary>操作情報から日本語の説明文を作る。</summary>
public static class Describer
{
    public static string Describe(Operation op) => op.Kind switch
    {
        OperationKind.Click => DescribeClick(op),
        OperationKind.TextInput => $"{Target(op.Element, "欄")}に「{op.Text}」と入力",
        OperationKind.KeyPress => $"{op.Keys} を押す",
        OperationKind.WindowShown => $"{Quote(op.Element?.Name)}画面が表示された",
        _ => "",
    };

    public static string Describe(IEnumerable<Operation> ops) =>
        string.Join("\n", ops.Select(Describe).Where(s => s.Length > 0));

    static string DescribeClick(Operation op)
    {
        var verb = (op.Button, op.DoubleClick) switch
        {
            (_, true) => "ダブルクリック",
            (MouseButton.Right, _) => "右クリック",
            (MouseButton.Middle, _) => "中クリック",
            _ => "クリック",
        };
        if (string.IsNullOrWhiteSpace(op.Element?.Name))
            return op.Point is { } p ? $"({p.X}, {p.Y}) を{verb}" : verb;
        return $"{Target(op.Element, ControlNoun(op.Element.ControlType))}を{verb}";
    }

    static string Target(ElementInfo? e, string noun) =>
        string.IsNullOrWhiteSpace(e?.Name) ? $"入力{noun}" : $"『{e.Name}』{noun}";

    static string Quote(string? name) => string.IsNullOrWhiteSpace(name) ? "" : $"『{name}』";

    /// <summary>UIA の ControlType 名(英語)を説明文用の名詞にする。</summary>
    static string ControlNoun(string controlType) => controlType switch
    {
        "Button" => "ボタン",
        "CheckBox" => "チェックボックス",
        "RadioButton" => "ラジオボタン",
        "ComboBox" => "コンボボックス",
        "Edit" or "Document" => "欄",
        "Hyperlink" => "リンク",
        "ListItem" => "項目",
        "MenuItem" => "メニュー",
        "TabItem" => "タブ",
        "TreeItem" => "項目",
        "DataItem" => "行",
        _ => "",
    };
}
