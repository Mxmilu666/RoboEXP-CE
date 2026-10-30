using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using RoboExp.App.Localization;

namespace RoboExp.App.Views;

/// <summary>
/// Original CompileProgressDlg: blinking tip ("Compiling..."), Cancel aborts. The work runs while the dialog is shown;
/// the dialog closes itself when the work finishes. Closing the window counts as Cancel.
/// </summary>
public sealed class ProgressDialog : Window
{
    private readonly CancellationTokenSource _cts = new();
    private bool _finished;

    private ProgressDialog(string title, string tip)
    {
        Title = title;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var tipText = new TextBlock { Text = tip, Margin = new Thickness(0, 0, 0, 8) };
        var blink = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        blink.Tick += (_, _) =>
        {
            tipText.Opacity = tipText.Opacity > 0 ? 0 : 1;
            blink.Interval = TimeSpan.FromMilliseconds(tipText.Opacity > 0 ? 800 : 1000);
        };
        Opened += (_, _) => blink.Start();
        Closed += (_, _) => blink.Stop();

        var cancel = new Button
        {
            Content = Localizer.Instance["CompileProgressDlg:btnCancel.Text"],
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 80,
            IsCancel = true,
        };
        cancel.Click += (_, _) => _cts.Cancel();

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                tipText,
                new ProgressBar { IsIndeterminate = true, Height = 6, Margin = new Thickness(0, 0, 0, 12) },
                cancel,
            },
        };
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_finished) return;
        e.Cancel = true; // keep the dialog until the work observed the cancellation
        _cts.Cancel();
    }

    public static async Task RunAsync(Window owner, string title, string tip, Func<CancellationToken, Task> work)
    {
        var dialog = new ProgressDialog(title, tip);
        var shown = dialog.ShowDialog(owner);
        try { await work(dialog._cts.Token); }
        finally
        {
            dialog._finished = true;
            dialog.Close();
            await shown;
            dialog._cts.Dispose();
        }
    }
}
