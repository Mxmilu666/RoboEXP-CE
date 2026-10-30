using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using RoboExp.App.ViewModels;

namespace RoboExp.App.Views;

public partial class OutputView : UserControl
{
    public OutputView() => InitializeComponent();

    private OutputViewModel? Vm => DataContext as OutputViewModel;

    /// <summary>Original listView_DoubleClick: jump to file/line of the row.</summary>
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is OutputRow row) Vm?.Activate(row);
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedRowText is { } text && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }
}
