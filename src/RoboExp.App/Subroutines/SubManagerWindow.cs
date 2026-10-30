using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using RoboExp.App.Localization;
using RoboExp.App.Views;
using RoboExp.Core.Model;
using RoboExp.Core.Project;

namespace RoboExp.App.Subroutines;

/// <summary>
/// Port of SubManagerDlg: list (icon, caption, name) of the non-system entries of the project's used-function list.
/// The status line shows the selected sub's file in the project directory.
/// </summary>
internal sealed class SubManagerWindow : Window
{
    private readonly ProjectSession _session;
    private readonly Func<ProjectInfo, bool> _isUsedByIcon;
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Single };
    private readonly TextBlock _status = new() { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 6, 2, 0) };
    private readonly Button _open, _delete, _up, _down;
    private IReadOnlyList<PrivateSub> _subs = [];
    private TextBox? _editor;

    public SubManagerWindow(ProjectSession session, Func<ProjectInfo, bool> isUsedByIcon)
    {
        _session = session;
        _isUsedByIcon = isUsedByIcon;
        var loc = Localizer.Instance;
        Title = loc.T("SubManagerDlg:$this.Text");
        Width = 460;
        Height = 380;
        MinWidth = 360;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var add = SubroutineDialogs.DialogButton(loc["SubManagerDlg:btnAdd.Text"], () => _ = AddAsync());
        _delete = SubroutineDialogs.DialogButton(loc["SubManagerDlg:btnDelete.Text"], () => _ = DeleteAsync());
        _open = SubroutineDialogs.DialogButton(loc["SubManagerDlg:btnOpen.Text"], Open);
        _up = SubroutineDialogs.DialogButton(loc["SubManagerDlg:button1.Text"], () => Move(-1));
        _down = SubroutineDialogs.DialogButton(loc["SubManagerDlg:button2.Text"], () => Move(+1));
        var close = SubroutineDialogs.DialogButton(loc["SubManagerDlg:btnClose.Text"], Close, isDefault: true, isCancel: true);

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("40,200,*"),
            Margin = new Thickness(4, 0, 4, 4),
            Children =
            {
                new TextBlock { Text = loc.T("Xwb:ProjectInfoFrm_Caption"), FontWeight = FontWeight.SemiBold, [Grid.ColumnProperty] = 1 },
                new TextBlock { Text = loc.T("Xwb:ProjectInfoFrm_Name"), FontWeight = FontWeight.SemiBold, [Grid.ColumnProperty] = 2 },
            },
        };

        _list.SelectionChanged += (_, _) => UpdateState();
        _list.DoubleTapped += (_, _) => BeginEdit();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.F2) { BeginEdit(); e.Handled = true; }
        };

        Content = new DockPanel
        {
            Margin = new Thickness(10),
            Children =
            {
                new StackPanel
                {
                    [DockPanel.DockProperty] = Dock.Right,
                    Margin = new Thickness(10, 0, 0, 0),
                    Spacing = 6,
                    Children = { add, _delete, _open, close, _up, _down },
                },
                new Border { [DockPanel.DockProperty] = Dock.Bottom, Child = _status },
                new Border { [DockPanel.DockProperty] = Dock.Top, Child = header },
                _list,
            },
        };

        Refresh(null);
    }

    /// <summary>Path to open after the dialog closed (Open button).</summary>
    public string? OpenPath { get; private set; }

    /// <summary>The project's used-function list or a sub's caption was modified.</summary>
    public bool Changed { get; private set; }

    private PrivateSub? Selected => _list.SelectedIndex >= 0 && _list.SelectedIndex < _subs.Count ? _subs[_list.SelectedIndex] : null;

    private string SubPath(ProjectInfo info) => SubroutineOps.SubRcuPath(_session.RcuPath, info);

    /// <summary>SubManagerDlg.RefreshTreeView, keeping the selection on <paramref name="select"/>.</summary>
    private void Refresh(ProjectInfo? select)
    {
        _editor = null;
        _subs = SubroutineOps.PrivateSubs(_session.Data, _session.Library.IsSystemFunction);
        _list.ItemsSource = _subs.Select(Row).ToList();
        _list.SelectedIndex = select == null ? -1 : _subs.ToList().FindIndex(s => ReferenceEquals(s.Info, select));
        UpdateState();
    }

    private static Control Row(PrivateSub sub)
    {
        var icon = SubroutineDialogs.ToBitmap(sub.Info.ProjectIcon);
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("40,200,*"),
            Background = Brushes.Transparent,
            [ToolTip.TipProperty] = sub.Info.ProjectPath,
            Children =
            {
                new Image { Source = icon, Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Left },
                new TextBlock { Text = sub.Info.ProjectCaption, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, [Grid.ColumnProperty] = 1 },
                new TextBlock { Text = sub.Info.ProjectName, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, [Grid.ColumnProperty] = 2 },
            },
        };
    }

    /// <summary>listView1_MouseUp: buttons enabled and status = sub file only while an item is selected.</summary>
    private void UpdateState()
    {
        var sel = Selected;
        _open.IsEnabled = _delete.IsEnabled = _up.IsEnabled = _down.IsEnabled = sel != null;
        _status.Text = sel == null ? "" : SubPath(sel.Info);
    }

    private void Open()
    {
        if (Selected is not { } sel) return;
        OpenPath = SubPath(sel.Info);
        Close();
    }

    private async Task DeleteAsync()
    {
        if (Selected is not { } sel) return;
        var loc = Localizer.Instance;
        if (_isUsedByIcon(sel.Info))
        {
            await MessageDialog.ShowAsync(this, loc.T("CannotDeleteUsingIcon"), loc.T("RoboExp"), MessageDialog.Buttons.Ok);
            return;
        }
        if (SubroutineOps.Remove(_session.Data, sel.Info.ProjectName ?? "")) Changed = true;
        Refresh(null);
    }

    /// <summary>btnAdd_Click: MenuRefSub, then select the last entry.</summary>
    private async Task AddAsync()
    {
        if (!await SubroutineDialogs.ReferenceSubAsync(this, _session)) return;
        Changed = true;
        Refresh(null);
        if (_subs.Count > 0) _list.SelectedIndex = _subs.Count - 1;
    }

    private void Move(int delta)
    {
        if (Selected is not { } sel) return;
        if (!SubroutineOps.Move(_session.Data, _subs, _list.SelectedIndex, delta)) return;
        Changed = true;
        Refresh(sel.Info);
        _list.Focus();
    }

    /// <summary>LabelEdit on the caption column; Enter commits (listView1_AfterLabelEdit), Escape cancels.</summary>
    private void BeginEdit()
    {
        if (Selected is not { } sel || _editor != null) return;
        if (_list.ContainerFromIndex(_list.SelectedIndex) is not ListBoxItem { Content: Grid row }) return;
        var editor = new TextBox { Text = sel.Info.ProjectCaption, [Grid.ColumnProperty] = 1, VerticalAlignment = VerticalAlignment.Center };
        _editor = editor;
        row.Children.Add(editor);
        var done = false;
        void Finish(bool commit)
        {
            if (done) return;
            done = true;
            var text = editor.Text;
            if (commit && text != null && text != sel.Info.ProjectCaption) Commit(sel, text);
            else Refresh(sel.Info);
        }
        editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Finish(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
        };
        editor.LostFocus += (_, _) => Finish(true);
        editor.AttachedToVisualTree += (_, _) => { editor.Focus(); editor.SelectAll(); };
    }

    private void Commit(PrivateSub sub, string caption)
    {
        try
        {
            SubroutineOps.RenameCaption(sub.Info, caption, _session.RcuPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ = MessageDialog.ShowAsync(this, e.Message, Localizer.Instance.T("RoboExp"), MessageDialog.Buttons.Ok);
        }
        Changed = true;
        Refresh(sub.Info);
    }
}
