using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using RoboExp.App.Localization;
using RoboExp.App.Services;
using RoboExp.App.Views;
using RoboExp.Core.Model;

namespace RoboExp.App.NewProject;

/// <summary>What FileNewFrm.GetProjectInfo produced.</summary>
public sealed record NewProjectRequest(ProjectType Kind, string RcuPath, string Description);

/// <summary>
/// Original FileNewFrm: project type tree (Flow / Code) × template list (Application / Subroutine), name, location,
/// description. With <c>subDir</c> (Project → New Subroutine) only Subroutine is allowed and the location is fixed.
/// </summary>
public sealed class NewProjectWindow : Window
{
    private readonly ListBox _types = new() { Name = "tvProjectType", MinWidth = 130 };
    private readonly ListBox _templates = new() { Name = "lst_FileNew", MinWidth = 180 };
    private readonly TextBox _name = new() { Name = "txt_FileName" };
    private readonly TextBox _path = new() { Name = "txt_Path" };
    private readonly TextBox _desc = new() { AcceptsReturn = true, Height = 54, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly string? _subDir;
    private readonly Func<string, bool> _isSystemFunction;
    private readonly IDialogService _dialogs;

    public NewProjectRequest? Result { get; private set; }

    public NewProjectWindow(string defaultDirectory, string? subDir, Func<string, bool> isSystemFunction, IDialogService dialogs, bool codeOnly = false)
    {
        _subDir = string.IsNullOrEmpty(subDir) ? null : subDir;
        _isSystemFunction = isSystemFunction;
        _dialogs = dialogs;
        var l = Localizer.Instance;
        Title = l.T("FileNewFrm:$this.Text");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _types.ItemsSource = new[] { l.T("Port:New.Flow"), l.T("Port:New.Code") };
        _templates.ItemsSource = new[] { l.T("Xwb:Application"), l.T("Xwb:Subroutine") };
        _types.SelectedIndex = codeOnly ? 1 : 0;
        _templates.SelectedIndex = _subDir != null ? 1 : 0;
        _path.Text = _subDir ?? defaultDirectory;
        if (_subDir != null)
        {
            _templates.IsEnabled = false;
            _path.IsEnabled = false;
        }
        _templates.SelectionChanged += (_, _) => NewFileName();
        _path.TextChanged += (_, _) => NewFileName();
        NewFileName();

        var browse = new Button { Content = l.T("FileNewFrm:btn_Browse.Text"), IsEnabled = _subDir == null };
        browse.Click += async (_, _) =>
        {
            var dir = await _dialogs.PickFolderAsync(l.T("FileNewFrm:lab_Path.Text"), _path.Text);
            if (dir != null) _path.Text = dir;
        };
        var ok = new Button { Name = "btn_Ok", Content = l.T("FileNewFrm:btn_Ok.Text"), MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = l.T("FileNewFrm:btn_Cancel.Text"), MinWidth = 80, IsCancel = true };
        ok.Click += async (_, _) => await OnOk();
        cancel.Click += (_, _) => Close();

        Grid Row(string labelKey, Control c, Control? extra = null)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*,Auto"), Margin = new Thickness(0, 2) };
            g.Children.Add(new TextBlock { Text = l.T(labelKey), VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(c, 1);
            g.Children.Add(c);
            if (extra != null) { Grid.SetColumn(extra, 2); extra.Margin = new Thickness(4, 0, 0, 0); g.Children.Add(extra); }
            return g;
        }

        var lists = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Height = 130 };
        var left = new DockPanel { Margin = new Thickness(0, 0, 8, 0) };
        left.Children.Add(new TextBlock { Text = l.T("FileNewFrm:label1.Text"), [DockPanel.DockProperty] = Dock.Top });
        left.Children.Add(_types);
        var right = new DockPanel();
        right.Children.Add(new TextBlock { Text = l.T("FileNewFrm:lab_new.Text"), [DockPanel.DockProperty] = Dock.Top });
        right.Children.Add(_templates);
        Grid.SetColumn(right, 1);
        lists.Children.Add(left);
        lists.Children.Add(right);

        Content = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 6,
            Children =
            {
                lists,
                Row("FileNewFrm:lab_Name.Text", _name),
                Row("FileNewFrm:lab_Path.Text", _path, browse),
                Row("FileNewFrm:lab_Desc.Text", _desc),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };
        Opened += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }

    private bool Subroutine => _templates.SelectedIndex == 1;
    private bool Code => _types.SelectedIndex == 1;

    private void NewFileName()
    {
        var dir = (_path.Text ?? "").Trim();
        if (dir.Length == 0 || !Path.IsPathFullyQualified(dir)) return;
        _name.Text = NewProjectRules.DefaultName(dir, Subroutine, _subDir != null);
    }

    private async Task OnOk()
    {
        var dir = (_path.Text ?? "").Trim().TrimEnd(Path.DirectorySeparatorChar);
        var name = (_name.Text ?? "").Trim();
        var err = NewProjectRules.Validate(dir, name, Subroutine, _subDir != null, _isSystemFunction);
        if (err != NewProjectRules.Error.None)
        {
            await MessageDialog.ShowAsync(this, Localizer.Instance.T(NewProjectRules.MessageKey(err, Subroutine)), Localizer.Instance.T("RoboExp"), MessageDialog.Buttons.Ok);
            _name.Focus();
            return;
        }
        Result = new NewProjectRequest(NewProjectRules.KindOf(Code, Subroutine), NewProjectRules.TargetPath(dir, name, _subDir != null), _desc.Text ?? "");
        Close();
    }
}
