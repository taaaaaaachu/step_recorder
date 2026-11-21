namespace SampleTarget;

/// <summary>E2E テストで操作される小さな WinForms アプリ。</summary>
static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        var form = new Form { Text = "サンプル画面", ClientSize = new(360, 140), StartPosition = FormStartPosition.CenterScreen };
        var label = new Label { Text = "氏名", Location = new(16, 20), AutoSize = true, TabIndex = 0 };
        var name = new TextBox { Name = "nameBox", Location = new(80, 16), Width = 250, TabIndex = 1 };
        var ok = new Button { Name = "okButton", Text = "OK", Location = new(255, 90), TabIndex = 2 };
        ok.Click += (_, _) =>
        {
            using var dlg = new Form { Text = "確認", ClientSize = new(240, 90), StartPosition = FormStartPosition.CenterParent };
            var close = new Button { Text = "閉じる", DialogResult = DialogResult.Cancel, Location = new(80, 40) };
            dlg.Controls.Add(close);
            dlg.CancelButton = close;
            dlg.ShowDialog(form);
        };
        form.Controls.AddRange([label, name, ok]);
        Application.Run(form);
    }
}
