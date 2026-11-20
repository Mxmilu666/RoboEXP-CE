using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using RoboExp.App.Localization;

namespace RoboExp.App.Mcp;

public sealed class McpConnectionWindow : Window
{
    private readonly McpController _controller;
    private readonly TextBox _prompt;
    private readonly TextBox _json;
    private readonly Button _toggle;
    private readonly Button _copyPrompt;
    private readonly Button _copyJson;
    private readonly TextBlock _status;

    public McpConnectionWindow(McpController controller)
    {
        _controller = controller;
        Title = T("Mcp.Title");
        Width = 720;
        Height = 600;
        MinWidth = 480;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        _status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        _prompt = ConnectionBox("McpPrompt", "Mcp.PromptLabel", TextWrapping.Wrap);
        _json = ConnectionBox("McpJson", "Mcp.JsonLabel", TextWrapping.NoWrap);
        _toggle = new Button { Name = "McpToggle", MinWidth = 100 };
        _copyPrompt = CopyButton("McpCopyPrompt", "Mcp.Copy", _prompt);
        _copyJson = CopyButton("McpCopyJson", "Mcp.CopyJson", _json);
        _toggle.Click += async (_, _) =>
        {
            try
            {
                if (_controller.IsEnabled) await _controller.DisposeAsync(); else _controller.Start();
                Refresh();
            }
            catch { _status.Text = T("Mcp.Failed"); }
        };
        var close = new Button { Content = T("Setup.Close"), IsCancel = true };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _toggle, close } };
        var grid = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,Auto,*,*,Auto"), RowSpacing = 12 };
        grid.Children.Add(new TextBlock { Text = T("Mcp.Help"), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        Grid.SetRow(_status, 1); grid.Children.Add(_status);
        var promptSection = Section("Mcp.PromptLabel", _copyPrompt, _prompt);
        var jsonSection = Section("Mcp.JsonLabel", _copyJson, _json);
        Grid.SetRow(promptSection, 2); grid.Children.Add(promptSection);
        Grid.SetRow(jsonSection, 3); grid.Children.Add(jsonSection);
        Grid.SetRow(buttons, 4); grid.Children.Add(buttons);
        Content = grid;
        controller.Changed += OnChanged;
        Closed += (_, _) => controller.Changed -= OnChanged;
        Refresh();
    }

    private static TextBox ConnectionBox(string name, string label, TextWrapping wrapping)
    {
        var box = new TextBox
        {
            Name = name, IsReadOnly = true, AcceptsReturn = true, TextWrapping = wrapping,
            FontFamily = new FontFamily("Cascadia Mono, Menlo, Consolas, DejaVu Sans Mono, monospace"),
            PlaceholderText = T("Mcp.Empty"),
        };
        Avalonia.Automation.AutomationProperties.SetName(box, T(label));
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(box, wrapping == TextWrapping.NoWrap ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
        return box;
    }

    private Button CopyButton(string name, string label, TextBox source)
    {
        var button = new Button { Name = name, Content = T(label) };
        button.Click += async (_, _) =>
        {
            if (_controller.IsEnabled && Clipboard is { } clipboard && source.Text is { Length: > 0 } text)
                await clipboard.SetTextAsync(text);
        };
        return button;
    }

    private static DockPanel Section(string label, Button copy, TextBox content)
    {
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 6) };
        heading.Children.Add(new TextBlock { Text = T(label), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(copy, 1);
        heading.Children.Add(copy);
        DockPanel.SetDock(heading, Dock.Top);
        return new DockPanel { Children = { heading, content } };
    }

    private void OnChanged(object? sender, EventArgs e) => Refresh();
    private void Refresh()
    {
        _toggle.Content = T(_controller.IsEnabled ? "Mcp.Stop" : "Mcp.Start");
        _copyPrompt.IsEnabled = _copyJson.IsEnabled = _controller.IsEnabled;
        var (prompt, json) = _controller.ConnectionDetails;
        _prompt.Text = prompt;
        _json.Text = json;
        _status.Text = T(_controller.IsEnabled ? "Mcp.Enabled" : "Mcp.Disabled");
    }
    private static string T(string key) => Localizer.Instance.T("Port:" + key);
}
