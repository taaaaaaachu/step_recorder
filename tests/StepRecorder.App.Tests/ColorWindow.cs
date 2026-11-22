using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using StepRecorder.Core;

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

    public static ColorWindow Cover(DisplayMonitor m, Color color) => new(color, m.Bounds.X, m.Bounds.Y, m.Bounds.Width, m.Bounds.Height);

    /// <summary>window を覆いより手前に出す(入力も前面の切り替えも無し)。</summary>
    public static void Raise(nint window)
    {
        SetWindowPos(window, -1 /*HWND_TOPMOST*/, 0, 0, 0, 0, 0x0013 /*NOSIZE | NOMOVE | NOACTIVATE*/);
        Desktop.Wait(300);
    }

    /// <summary>
    /// 実際の画面が写らないことを、撮る前に確かめる。覆いは最前面にしないと、使用中のウィンドウの下に潜る。
    /// 見えている所を格子で調べ、このテストか allowed のプロセス以外のウィンドウが出ていれば中止する。
    /// </summary>
    public static void EnsureCovered(DisplayMonitor m, params uint[] allowed)
    {
        for (var y = 4; y < m.Bounds.Height; y += 24)
            for (var x = 4; x < m.Bounds.Width; x += 24)
            {
                var window = Native.WindowFromPoint(new Native.POINT { X = m.Bounds.X + x, Y = m.Bounds.Y + y });
                var pid = Native.ProcessId(window);
                if (pid != Environment.ProcessId && !allowed.Contains(pid))
                    throw new InvalidOperationException($"{m.Label} を覆えていないので、実際の画面が写らないよう撮る前に中止した({x},{y} に {Native.ClassName(window)})");
            }
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
