using Avalonia.Controls;
using Avalonia.Input;

namespace RoboExp.App.Views;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        var version = typeof(SplashWindow).Assembly.GetName().Version;
        VersionLabel.Text = $"Version {version?.ToString(3) ?? "0.1.0"}";
    }

    public void SetStatus(string text) => StatusLabel.Text = text;

    private void OnDragWindow(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
}
