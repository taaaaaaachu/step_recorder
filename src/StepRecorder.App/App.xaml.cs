using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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
        ComponentDispatcher.ThreadPreprocessMessage += SwitchImeByLangKey;
        var main = new MainWindow();
        app.MainWindow = main;
        // StepRecorder.exe <記録フォルダ> なら開始画面を出さずに編集画面を開く
        if (args is [var dir, ..] && File.Exists(Path.Combine(dir, SessionStore.FileName)))
            main.OpenSession(Path.GetFullPath(dir));
        else
            main.Show();
        app.Run();
    }

    /// <summary>
    /// WPF の TextBox では、キーボードの LANG1/LANG2(IME オン/オフ)が IME に効かないので、自分で切り替える。
    /// VK_IME_ON/OFF は WPF の Key に無く KeyEventArgs では見分けられないため、メッセージの段階で拾う。
    /// IME を使わない欄(ショートカットキー欄など)は除く。
    /// </summary>
    static void SwitchImeByLangKey(ref MSG msg, ref bool handled)
    {
        const int WM_KEYDOWN = 0x0100;
        if (handled || msg.message != WM_KEYDOWN || ImeKeys.OpenState((int)msg.wParam) is not { } open) return;
        if (Keyboard.FocusedElement is not TextBox box || !InputMethod.GetIsInputMethodEnabled(box)) return;
        Native.SetImeOpen(msg.hwnd, open);
        handled = true;
    }
}
