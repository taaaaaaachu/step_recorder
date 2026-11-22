using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace StepRecorder.App.Tests;

/// <summary>一色で塗りつぶした最前面のウィンドウ。入力も前面の切り替えも無しに、何が写ったかを色で確かめる。</summary>
sealed class ColorWindow : IDisposable
{
    readonly Thread thread;
    Window window = null!;

    public nint Handle { get; private set; }

    /// <summary>(x, y, width, height) は物理ピクセル。</summary>
    public ColorWindow(Color color, int x = 120, int y = 120, int width = 400, int height = 300)
    {
        using var ready = new ManualResetEventSlim();
        thread = new Thread(() =>
        {
            window = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowActivated = false,
                ShowInTaskbar = false,
                Topmost = true,
                Background = new SolidColorBrush(color),
            };
            window.Show();
            Handle = new WindowInteropHelper(window).Handle;
            // 拡大率の違うモニターへ移ると大きさが変わるので、移してから大きさを入れ直す
            for (var i = 0; i < 2; i++) SetWindowPos(Handle, 0, x, y, width, height, 0x0014 /*NOZORDER | NOACTIVATE*/);
            window.Dispatcher.BeginInvoke(ready.Set, DispatcherPriority.ApplicationIdle);
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        ready.Wait();
        Desktop.Wait(700); // 開くアニメーションが終わるのを待つ
    }

    public void Paint(Color color) => window.Dispatcher.Invoke(() => window.Background = new SolidColorBrush(color));

    /// <summary>このウィンドウのスレッドで動かす。</summary>
    public void Invoke(Action<Window> action) => window.Dispatcher.Invoke(() => action(window));

    public void Dispose()
    {
        window.Dispatcher.Invoke(window.Close);
        window.Dispatcher.InvokeShutdown();
        thread.Join();
    }

    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
}
