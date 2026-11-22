using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StepRecorder.App;

/// <summary>時間のかかる処理の間に出す、文言とプログレスバーだけの小さなウインドウ。処理の途中では閉じさせない。</summary>
sealed class ProgressWindow : Window
{
    readonly TextBlock label = new() { Margin = new Thickness(0, 0, 0, 8) };
    readonly ProgressBar bar = new() { Maximum = 1, Width = 380, Height = 18, IsIndeterminate = true };
    bool busy = true;

    public ProgressWindow(string title, string text, Window? owner)
    {
        Title = title;
        Owner = owner;
        ShowInTaskbar = owner == null;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Yu Gothic UI");
        FontSize = 13;
        label.Text = text;
        Content = new StackPanel { Margin = new Thickness(18), Children = { label, bar } };
        Closing += (_, e) => e.Cancel = busy;
    }

    /// <summary>どのスレッドからでも呼べる。value(0〜1)が null なら、どこまで進んだか分からない表示にする。</summary>
    public void Report(string text, double? value = null) => Dispatcher.BeginInvoke(() =>
    {
        label.Text = text;
        bar.IsIndeterminate = value == null;
        if (value is { } v) bar.Value = v;
    });

    public void Finish()
    {
        busy = false;
        Close();
    }
}
