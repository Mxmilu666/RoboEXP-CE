using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace RoboExp.App.Variables;

public partial class VariablesView : UserControl
{
    public VariablesView() => AvaloniaXamlLoader.Load(this);

    private static VariableRowViewModel? Row(object? sender) => (sender as Control)?.DataContext as VariableRowViewModel;

    private async void OnNameCommit(object? sender, RoutedEventArgs e) { if (Row(sender) is { } r) await r.CommitNameAsync(); }
    private async void OnNameKey(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter && Row(sender) is { } r) await r.CommitNameAsync(); }
    private async void OnDefaultCommit(object? sender, RoutedEventArgs e) { if (Row(sender) is { } r) await r.CommitDefaultAsync(); }
    private async void OnDefaultKey(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter && Row(sender) is { } r) await r.CommitDefaultAsync(); }
    private void OnHintCommit(object? sender, RoutedEventArgs e) => Row(sender)?.CommitHint();
}
