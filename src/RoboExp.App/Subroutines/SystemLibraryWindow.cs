using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RoboExp.App.Localization;
using RoboExp.App.Views;
using RoboExp.Core.Library;

namespace RoboExp.App.Subroutines;

/// <summary>
/// Port of SystemLibDlg: the library tree (bands in blue; entries whose function is missing on light gray) with
/// New Band, Add Sub, Delete, Show/Hide, Up/Down, Default; OK keeps the edits (caller saves), Cancel discards them.
/// </summary>
internal sealed class SystemLibraryWindow : Window
{
    private readonly UserLibrary _library;
    private readonly TreeView _tree = new() { SelectionMode = SelectionMode.Single };
    private readonly HashSet<UserBand> _expanded = [];
    private readonly Button _addFunc, _delete, _show, _hide, _up, _down;
    private object? _selected;
    private bool _refreshing;

    public SystemLibraryWindow(UserLibrary library)
    {
        _library = library;
        var loc = Localizer.Instance;
        Title = loc.T("SystemLibDlg:$this.Text");
        Width = 520;
        Height = 460;
        MinWidth = 400;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var newBand = SubroutineDialogs.DialogButton(loc["SystemLibDlg:btnNewBand.Text"], () => _ = NewBandAsync());
        _addFunc = SubroutineDialogs.DialogButton(loc["SystemLibDlg:btnAddFunc.Text"], () => _ = AddFunctionAsync());
        _delete = SubroutineDialogs.DialogButton(loc["SystemLibDlg:btnDelete.Text"], Delete);
        _show = SubroutineDialogs.DialogButton(loc["SystemLibDlg:btnShow.Text"], () => SetShown(true));
        _hide = SubroutineDialogs.DialogButton(loc["SystemLibDlg:btnHide.Text"], () => SetShown(false));
        _up = SubroutineDialogs.DialogButton(loc["SystemLibDlg:btnUp.Text"], () => Move(up: true));
        _down = SubroutineDialogs.DialogButton(loc["SystemLibDlg:btnDown.Text"], () => Move(up: false));
        var reset = SubroutineDialogs.DialogButton(loc["SystemLibDlg:btnDefault.Text"], _library.ResetSystemDefault);
        var ok = SubroutineDialogs.DialogButton(loc["SystemLibDlg:btnOK.Text"], () => { Accepted = true; Close(); }, isDefault: true);
        var cancel = SubroutineDialogs.DialogButton(loc["SystemLibDlg:btnCancel.Text"], Close, isCancel: true);

        _library.Changed += (_, _) =>
        {
            HasChange = true;
            Refresh();
        };
        _tree.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            _selected = (_tree.SelectedItem as TreeViewItem)?.Tag;
            UpdateButtons();
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
                    Children = { newBand, _addFunc, _delete, _show, _hide, _up, _down, reset, new Border { Height = 12 }, ok, cancel },
                },
                new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = _tree },
            },
        };

        Refresh();
    }

    /// <summary>Closed with OK.</summary>
    public bool Accepted { get; private set; }

    /// <summary>The tree was modified (m_HasChange).</summary>
    public bool HasChange { get; private set; }

    /// <summary>RefreshTreeView + SetNodeSelected(<see cref="_selected"/>).</summary>
    private void Refresh()
    {
        _refreshing = true;
        var items = new List<TreeViewItem>();
        TreeViewItem? selectedItem = null;
        foreach (var band in _library.Bands)
        {
            var bandItem = new TreeViewItem
            {
                Header = NodeHeader(_library.CaptionOf(band), Brushes.Blue, null, band.IsShown, null),
                Tag = band,
                IsExpanded = _expanded.Contains(band),
            };
            bandItem.PropertyChanged += (_, e) =>
            {
                if (e.Property != TreeViewItem.IsExpandedProperty) return;
                if (bandItem.IsExpanded) _expanded.Add(band);
                else _expanded.Remove(band);
            };
            if (ReferenceEquals(band, _selected)) selectedItem = bandItem;
            foreach (var f in band.Functions)
            {
                var funcItem = new TreeViewItem
                {
                    Header = NodeHeader(f.Path, null, f.IsLoaded ? null : Brushes.LightGray, f.IsShown, ToolTipOf(f)),
                    Tag = f,
                };
                bandItem.Items.Add(funcItem);
                if (ReferenceEquals(f, _selected))
                {
                    selectedItem = funcItem;
                    bandItem.IsExpanded = true;
                }
            }
            items.Add(bandItem);
        }
        _tree.ItemsSource = items;
        _tree.SelectedItem = selectedItem;
        if (selectedItem == null) _selected = null;
        _refreshing = false;
        if (selectedItem != null)
        {
            selectedItem.BringIntoView();
            _tree.Focus();
        }
        UpdateButtons();
    }

    private string? ToolTipOf(UserLibraryFunction f)
    {
        if (f.ProjectInfo is { } info) return info.ProjectCaption + "\n" + info.GetFuncDeclareCodeLine();
        if (f.SystemFunction is { } sys) return sys.Caption.Get(_library.Language);
        return null;
    }

    /// <summary>Hidden entries are drawn half transparent (the original showed no difference besides Show/Hide state).</summary>
    private static Control NodeHeader(string text, IBrush? foreground, IBrush? background, bool shown, string? toolTip)
    {
        var block = new TextBlock { Text = text, Opacity = shown ? 1 : 0.5, Padding = new Thickness(2, 0) };
        if (foreground != null) block.Foreground = foreground;
        if (background != null) block.Background = background;
        if (toolTip != null) ToolTip.SetTip(block, toolTip);
        return block;
    }

    /// <summary>SystemLibDlg.UpdateButtonsState.</summary>
    private void UpdateButtons()
    {
        switch (_selected)
        {
            case UserBand band:
                _addFunc.IsEnabled = _up.IsEnabled = _down.IsEnabled = true;
                _delete.IsEnabled = _library.CanRemoveBand(band);
                _hide.IsEnabled = band.IsShown;
                _show.IsEnabled = !band.IsShown;
                break;
            case UserLibraryFunction f:
                _addFunc.IsEnabled = _up.IsEnabled = _down.IsEnabled = true;
                _delete.IsEnabled = _library.CanRemoveFunction(f);
                _hide.IsEnabled = f.IsShown;
                _show.IsEnabled = !f.IsShown;
                break;
            default:
                _addFunc.IsEnabled = _delete.IsEnabled = _show.IsEnabled = _hide.IsEnabled = _up.IsEnabled = _down.IsEnabled = false;
                break;
        }
    }

    private async Task NewBandAsync()
    {
        var loc = Localizer.Instance;
        var caption = await SubroutineDialogs.InputAsync(this, loc.T("NewBand"), loc.T("BandName"), loc.T("NewBand"));
        if (string.IsNullOrEmpty(caption)) return;
        var band = _library.AddBand(caption);
        _selected = band;
        Refresh();
    }

    private async Task AddFunctionAsync()
    {
        var loc = Localizer.Instance;
        if (_library.Bands.Count == 0)
        {
            await MessageDialog.ShowAsync(this, loc.T("NewBandFirst"), Title ?? "", MessageDialog.Buttons.Ok);
            return;
        }
        var band = _selected switch
        {
            UserBand b => b,
            UserLibraryFunction f => _library.Bands.FirstOrDefault(b => b.Functions.Contains(f)),
            _ => null,
        };
        if (band == null)
        {
            await MessageDialog.ShowAsync(this, loc.T("SelectNodeFirst"), Title ?? "", MessageDialog.Buttons.Ok);
            return;
        }
        var path = await SubroutineDialogs.PickRcuAsync(this, loc.T("SystemLibDlg:btnAddFunc.Text"));
        if (path == null) return;
        var node = _library.AddFunction(band, path, out var errorKey);
        _selected = node;
        Refresh();
        if (errorKey != null) await MessageDialog.ShowAsync(this, loc.T(errorKey), Title ?? "", MessageDialog.Buttons.Ok);
    }

    private void Delete()
    {
        switch (_selected)
        {
            case UserBand band: _library.RemoveBand(band); break;
            case UserLibraryFunction f: _library.RemoveFunction(f); break;
            default: return;
        }
        _selected = null;
        Refresh();
    }

    private void SetShown(bool shown)
    {
        switch (_selected)
        {
            case UserBand band: _library.SetShown(band, shown); break;
            case UserLibraryFunction f: _library.SetShown(f, shown); break;
        }
    }

    private void Move(bool up)
    {
        switch (_selected)
        {
            case UserBand band: _ = up ? _library.MoveUp(band) : _library.MoveDown(band); break;
            case UserLibraryFunction f: _ = up ? _library.MoveUp(f) : _library.MoveDown(f); break;
        }
    }
}
