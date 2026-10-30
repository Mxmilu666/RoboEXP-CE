using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RoboExp.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow() => InitializeComponent();

    private void OnOk(object? sender, RoutedEventArgs e) => Close();
}
