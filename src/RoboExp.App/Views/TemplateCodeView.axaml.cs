using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RoboExp.App.ViewModels;

namespace RoboExp.App.Views;

public partial class TemplateCodeView : UserControl
{
    public TemplateCodeView() => InitializeComponent();

    private TemplateCodeViewModel? Vm => DataContext as TemplateCodeViewModel;

    private void OnInsert(object? sender, RoutedEventArgs e) => Vm?.RequestInsert(Vm.SelectedItem);

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is CodeTemplate t) Vm?.RequestInsert(t);
    }
}
