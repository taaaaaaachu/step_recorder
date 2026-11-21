namespace SampleTarget;

/// <summary>E2E テストで操作される小さな WinForms アプリ。</summary>
static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        var form = new Form { Text = "サンプル画面", ClientSize = new(380, 200), StartPosition = FormStartPosition.CenterScreen };
        var tabs = new TabControl { Dock = DockStyle.Fill };

        // 基本タブ: 入力欄と、確認ダイアログを開くボタン
        var basic = new TabPage("基本");
        var label = new Label { Text = "氏名", Location = new(16, 20), AutoSize = true, TabIndex = 0 };
        var name = new TextBox { Name = "nameBox", Location = new(80, 16), Width = 250, TabIndex = 1 };
        var ok = new Button { Name = "okButton", Text = "OK", Location = new(255, 120), TabIndex = 2 };
        ok.Click += (_, _) =>
        {
            using var dlg = new Form { Text = "確認", ClientSize = new(240, 90), StartPosition = FormStartPosition.CenterParent };
            var close = new Button { Text = "閉じる", DialogResult = DialogResult.Cancel, Location = new(80, 40) };
            dlg.Controls.Add(close);
            dlg.CancelButton = close;
            dlg.ShowDialog(form);
        };
        basic.Controls.AddRange([label, name, ok]);

        // 詳細タブ: ウィンドウはそのままで、中のパネルだけを差し替えるボタン
        var detail = new TabPage("詳細");
        var panelA = new Panel { Location = new(16, 16), Size = new(330, 80), BackColor = Color.LightSteelBlue };
        panelA.Controls.Add(new Label { Text = "一覧", Location = new(8, 8), AutoSize = true });
        var panelB = new Panel { Location = new(16, 16), Size = new(330, 80), BackColor = Color.Khaki, Visible = false };
        panelB.Controls.Add(new Label { Text = "編集", Location = new(8, 8), AutoSize = true });
        panelB.Controls.Add(new TextBox { Location = new(8, 36), Width = 200 });
        var toggle = new Button { Name = "toggleButton", Text = "表示切替", Location = new(255, 120) };
        toggle.Click += (_, _) => (panelA.Visible, panelB.Visible) = (panelB.Visible, panelA.Visible);
        detail.Controls.AddRange([panelA, panelB, toggle]);

        tabs.TabPages.AddRange([basic, detail]);
        form.Controls.Add(tabs);
        Application.Run(form);
    }
}
