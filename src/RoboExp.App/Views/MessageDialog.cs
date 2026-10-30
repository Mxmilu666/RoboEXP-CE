using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using RoboExp.App.Localization;
using RoboExp.App.Services;

namespace RoboExp.App.Views;

/// <summary>Modal message box (WinForms MessageBox replacement) with OK / Yes-No / Yes-No-Cancel buttons.</summary>
public sealed class MessageDialog : Window
{
    public enum Buttons { Ok, YesNo, YesNoCancel }

    private ConfirmResult _result = ConfirmResult.Cancel;

    private MessageDialog(string text, string title, Buttons buttons)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MinWidth = 320;
        MaxWidth = 640;
        ShowInTaskbar = false;

        var loc = Localizer.Instance;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        Button Add(string caption, ConfirmResult result, bool isDefault = false, bool isCancel = false)
        {
            var b = new Button { Content = caption, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = isDefault, IsCancel = isCancel };
            b.Click += (_, _) => { _result = result; Close(); };
            panel.Children.Add(b);
            return b;
        }
        if (buttons == Buttons.Ok) Add(loc["Options:btnOK.Text"], ConfirmResult.Yes, isDefault: true, isCancel: true);
        else
        {
            Add(loc["Port:Yes"], ConfirmResult.Yes, isDefault: true);
            Add(loc["Port:No"], ConfirmResult.No, isCancel: buttons == Buttons.YesNo);
            if (buttons == Buttons.YesNoCancel) Add(loc["Options:btnCancel.Text"], ConfirmResult.Cancel, isCancel: true);
        }
        _result = buttons == Buttons.YesNo ? ConfirmResult.No : ConfirmResult.Cancel;

        Content = new DockPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new Border { [DockPanel.DockProperty] = Dock.Bottom, Margin = new Thickness(0, 16, 0, 0), Child = panel },
                new SelectableTextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            },
        };
    }

    public static async Task<ConfirmResult> ShowAsync(Window owner, string text, string title, Buttons buttons)
    {
        var dialog = new MessageDialog(text, title, buttons);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
