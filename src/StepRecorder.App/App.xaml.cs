using System.Windows;
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
        app.Run();
    }
}
