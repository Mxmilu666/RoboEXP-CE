using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using RoboExp.App.Localization;

namespace RoboExp.App.Mcp;

public sealed class McpConnectionWindow : Window
{
    private readonly McpController _controller;
    private readonly TextBox _prompt;
    private readonly Button _toggle;
    private readonly Button _copy;
    private readonly TextBlock _status;

    public McpConnectionWindow(McpController controller)
    {
        _controller = controller;
        Title = T("Mcp.Title");
        Width = 660;
        Height = 520;
        MinWidth = 480;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        _status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        _prompt = new TextBox { Name = "McpPrompt", IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        _toggle = new Button { Name = "McpToggle", MinWidth = 100 };
        _copy = new Button { Name = "McpCopy", Content = T("Mcp.Copy"), MinWidth = 100 };
        _toggle.Click += async (_, _) =>
        {
            try
            {
                if (_controller.IsEnabled) await _controller.DisposeAsync(); else _controller.Start();
                Refresh();
            }
            catch { _status.Text = T("Mcp.Failed"); }
        };
        _copy.Click += async (_, _) =>
        {
            if (Clipboard is { } clipboard && _controller.IsEnabled) await clipboard.SetTextAsync(_controller.ConnectionPrompt);
        };
        var close = new Button { Content = T("Setup.Close"), IsCancel = true };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _toggle, _copy, close } };
        var grid = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), RowSpacing = 12 };
        grid.Children.Add(new TextBlock { Text = T("Mcp.Help"), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        Grid.SetRow(_status, 1); grid.Children.Add(_status);
        Grid.SetRow(_prompt, 2); grid.Children.Add(_prompt);
        Grid.SetRow(buttons, 3); grid.Children.Add(buttons);
        Content = grid;
        controller.Changed += OnChanged;
        Closed += (_, _) => controller.Changed -= OnChanged;
        Refresh();
    }

    private void OnChanged(object? sender, EventArgs e) => Refresh();
    private void Refresh()
    {
        _toggle.Content = T(_controller.IsEnabled ? "Mcp.Stop" : "Mcp.Start");
        _copy.IsEnabled = _controller.IsEnabled;
        _prompt.Text = _controller.ConnectionPrompt;
        _status.Text = T(_controller.IsEnabled ? "Mcp.Enabled" : "Mcp.Disabled");
    }
    private static string T(string key) => Localizer.Instance.T("Port:" + key);
}
