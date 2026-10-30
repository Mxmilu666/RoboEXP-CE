using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using RoboExp.App.Localization;

namespace RoboExp.App.Properties;

/// <summary>
/// Floating PropertyFrmNew: the per-function panel (SetTypeVisible 4..18) when the block has one, else the generic editor.
/// Pending text is committed when the window closes.
/// </summary>
public sealed class BlockEditorWindow : Window
{
    private readonly Action _commit;

    public BlockEditorWindow(BlockPropertiesViewModel vm)
    {
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Control body;
        if (vm.SpecialPanel != SpecialPanelKind.None)
        {
            var special = new SpecialPanelView(vm) { Margin = new Thickness(8) };
            Title = vm.SpecialTitle;
            SizeToContent = SizeToContent.WidthAndHeight;
            CanResize = false;
            body = special;
            _commit = special.CommitPending;
            SpecialView = special;
        }
        else
        {
            var view = new BlockPropertiesView { DataContext = vm };
            Title = vm.Title;
            Width = 380;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 640;
            CanResize = true;
            body = view;
            _commit = view.CommitPending;
        }
        var ok = new Button { Name = "BtnOk", Content = Localizer.Instance.T("Options:btnOK.Text"), MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(6) };
        ok.Click += (_, _) => Close();
        var root = new DockPanel();
        DockPanel.SetDock(ok, Dock.Bottom);
        root.Children.Add(ok);
        root.Children.Add(body);
        Content = root;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closing += (_, _) => _commit();
    }

    /// <summary>The per-function panel when shown (tests/screenshots).</summary>
    public SpecialPanelView? SpecialView { get; }
}
