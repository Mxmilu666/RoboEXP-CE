using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RoboExp.App.ViewModels;

namespace RoboExp.App.Views;

public partial class DownloadWindow : Window
{
    private readonly CancellationTokenSource _poll = new();
    private readonly DispatcherTimer _steps = new() { Interval = TimeSpan.FromSeconds(1) };

    public DownloadWindow()
    {
        InitializeComponent();
        _steps.Tick += (_, _) =>
        {
            if (DataContext is DownloadViewModel { IsDownloading: false } vm) vm.ActiveStep = (vm.ActiveStep + 1) % 3;
        };
        Opened += (_, _) => _steps.Start();
        Closed += (_, _) => _steps.Stop();
        Opened += async (_, _) =>
        {
            if (DataContext is DownloadViewModel vm) await vm.PollTargetsAsync(_poll.Token);
        };
    }

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DownloadViewModel vm)
            await Launcher.LaunchUriAsync(new Uri(vm.ProgramDirectory + Path.DirectorySeparatorChar));
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is DownloadViewModel vm) vm.CloseRequested += (_, _) => Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (DataContext is DownloadViewModel { IsDownloading: true } vm)
        {
            e.Cancel = true; // wait for the transport to observe cancellation; Start() closes afterwards
            vm.CancelCommand.Execute(null);
            return;
        }
        _poll.Cancel();
    }
}
