using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using RoboExp.App.Localization;

namespace RoboExp.App.Flow;

/// <summary>Original RoboExp.InputBox(caption, prompt, default): one text field, OK/Cancel.</summary>
public sealed class FlowInputBox : Window
{
    private readonly TextBox _text;
    private bool _ok;

    public FlowInputBox(string title, string prompt, string initial)
    {
        Title = title;
        Width = 320;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _text = new TextBox { Text = initial, Margin = new Thickness(0, 4, 0, 8) };
        var ok = new Button { Content = Localizer.Instance["InputBox:button1.Text"], IsDefault = true, MinWidth = 72 };
        var cancel = new Button { Content = Localizer.Instance["InputBox:button2.Text"], IsCancel = true, MinWidth = 72 };
        ok.Click += (_, _) =>
        {
            _ok = true;
            Close();
        };
        cancel.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(12),
            Children =
            {
                new TextBlock { Text = prompt },
                _text,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { ok, cancel } },
            },
        };
        Opened += (_, _) => _text.Focus();
    }

    /// <summary>The entered text, or null when cancelled.</summary>
    public static async Task<string?> ShowAsync(Window owner, string title, string prompt, string initial = "")
    {
        var box = new FlowInputBox(title, prompt, initial);
        await box.ShowDialog(owner);
        return box._ok ? box._text.Text ?? "" : null;
    }
}
