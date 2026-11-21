using System.IO;
using System.Windows;
using StepRecorder.Core;
using Velopack;

namespace StepRecorder.App;

public partial class App : Application
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run(); // インストール・更新時のフック。通常起動ならすぐ戻る
        var app = new App();
        app.InitializeComponent();
        var main = new MainWindow();
        app.MainWindow = main;
        // StepRecorder.exe <記録フォルダ> なら開始画面を出さずに編集画面を開く
        if (args is [var dir, ..] && File.Exists(Path.Combine(dir, SessionStore.FileName)))
            main.OpenSession(Path.GetFullPath(dir));
        else
            main.Show();
        app.Run();
    }
}
