using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RoboExp.App.Documents;
using RoboExp.App.Localization;
using RoboExp.App.Services;
using RoboExp.App.ViewModels;

namespace RoboExp.App.Views;

public partial class MainWindow : Window, IDialogService
{
    private MainWindowViewModel? _vm;
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        RecentMenu.SubmenuOpened += (_, _) => FillRecentMenu();
    }

    private MainWindowViewModel Vm => _vm ?? throw new InvalidOperationException("DataContext not set");

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _vm = DataContext as MainWindowViewModel;
        if (_vm == null) return;

        var icons = _vm.Panel(PanelIds.Icons);
        var output = _vm.Panel(PanelIds.Output);
        LeftContent.Bind(ContentProperty, new Binding(nameof(ToolPanel.Content)) { Source = icons });
        LeftTitle.Bind(TextBlock.TextProperty, new Binding(nameof(ToolPanel.Title)) { Source = icons });
        BottomContent.Bind(ContentProperty, new Binding(nameof(ToolPanel.Content)) { Source = output });
        BottomTitle.Bind(TextBlock.TextProperty, new Binding(nameof(ToolPanel.Title)) { Source = output });

        var layout = _vm.Env.Settings.Layout;
        Layout.ColumnDefinitions[0].Width = new GridLength(layout.LeftWidth);
        Layout.ColumnDefinitions[4].Width = new GridLength(layout.RightWidth);
        Layout.RowDefinitions[2].Height = new GridLength(layout.BottomHeight);
        foreach (var p in _vm.Panels) p.PropertyChanged += OnPanelChanged;
        ApplyPaneVisibility();
        RegisterHotKeys();
    }

    private void OnPanelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolPanel.IsVisible)) ApplyPaneVisibility();
    }

    private double _leftWidth = 220, _rightWidth = 260, _bottomHeight = 180;

    /// <summary>Collapses a pane's grid track (and its splitter) instead of removing it, so its size comes back on show.</summary>
    private void ApplyPaneVisibility()
    {
        var vm = Vm;
        var left = vm.Panel(PanelIds.Icons).IsVisible;
        var right = vm.RightPanels.Any(p => p.IsVisible);
        var bottom = vm.Panel(PanelIds.Output).IsVisible;
        Collapse(Layout.ColumnDefinitions[0], left, ref _leftWidth, vm.Env.Settings.Layout.LeftWidth);
        Collapse(Layout.ColumnDefinitions[4], right, ref _rightWidth, vm.Env.Settings.Layout.RightWidth);
        Collapse(Layout.RowDefinitions[2], bottom, ref _bottomHeight, vm.Env.Settings.Layout.BottomHeight);
        LeftPane.IsVisible = LeftSplitter.IsVisible = left;
        RightPane.IsVisible = RightSplitter.IsVisible = right;
        BottomPane.IsVisible = BottomSplitter.IsVisible = bottom;
    }

    private static void Collapse(ColumnDefinition col, bool visible, ref double remembered, double fallback)
    {
        if (!visible && col.Width.Value > 0) { remembered = col.Width.Value; col.Width = new GridLength(0); }
        else if (visible && col.Width.Value == 0) col.Width = new GridLength(remembered > 0 ? remembered : fallback);
    }

    private static void Collapse(RowDefinition row, bool visible, ref double remembered, double fallback)
    {
        if (!visible && row.Height.Value > 0) { remembered = row.Height.Value; row.Height = new GridLength(0); }
        else if (visible && row.Height.Value == 0) row.Height = new GridLength(remembered > 0 ? remembered : fallback);
    }

    private void OnHideLeft(object? sender, RoutedEventArgs e) => Vm.Panel(PanelIds.Icons).IsVisible = false;
    private void OnHideBottom(object? sender, RoutedEventArgs e) => Vm.Panel(PanelIds.Output).IsVisible = false;

    private void OnHideRight(object? sender, RoutedEventArgs e)
    {
        foreach (var p in Vm.RightPanels) p.IsVisible = false;
    }

    private async void OnCloseDocumentClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DocumentViewModel doc) await Vm.CloseDocumentAsync(doc);
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Original shortcut table (Keys values in InitToolBarAndMenu); Ctrl becomes Cmd on macOS.</summary>
    private void RegisterHotKeys()
    {
        var cmd = Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        void Hot(MenuItem item, Key key, KeyModifiers mods, bool handledByEditor = false)
        {
            var gesture = new KeyGesture(key, mods);
            item.InputGesture = gesture;
            if (!handledByEditor) item.HotKey = gesture;
        }
        Hot(MenuNew, Key.N, cmd);
        Hot(MenuOpen, Key.O, cmd);
        Hot(MenuSave, Key.S, cmd);
        Hot(MenuSaveAs, Key.S, cmd | KeyModifiers.Shift);
        Hot(MenuUndo, Key.Z, cmd, handledByEditor: true);
        Hot(MenuRedo, Key.Y, cmd, handledByEditor: true);
        Hot(MenuCut, Key.X, cmd, handledByEditor: true);
        Hot(MenuCopy, Key.C, cmd, handledByEditor: true);
        Hot(MenuPaste, Key.V, cmd, handledByEditor: true);
        Hot(MenuSelectAll, Key.A, cmd, handledByEditor: true);
        Hot(MenuFind, Key.F, cmd, handledByEditor: true);
        Hot(MenuReplace, Key.H, cmd, handledByEditor: true);
        Hot(MenuCompile, Key.F9, KeyModifiers.None);
        Hot(MenuDownload, Key.F10, KeyModifiers.None);
    }

    /// <summary>Original MenuRecentFiles_Click: "&amp;1 path" items, separator, Clear Recent Files List.</summary>
    private void FillRecentMenu()
    {
        var vm = Vm;
        RecentMenu.Items.Clear();
        var n = 1;
        foreach (var path in vm.RecentFiles)
        {
            RecentMenu.Items.Add(new MenuItem
            {
                Header = Localizer.ToAccessText("&" + n++ + " " + path),
                Command = vm.OpenRecentCommand,
                CommandParameter = path,
            });
        }
        if (n > 1) RecentMenu.Items.Add(new Separator());
        RecentMenu.Items.Add(new MenuItem { Header = Localizer.Instance["ClearRecentFilesList"], Command = vm.ClearRecentFilesCommand });
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || _vm == null) return;
        e.Cancel = true;
        if (!await _vm.CloseAllDocumentsAsync()) return;
        var layout = _vm.Env.Settings.Layout;
        if (Layout.ColumnDefinitions[0].Width.Value > 0) layout.LeftWidth = Layout.ColumnDefinitions[0].Width.Value;
        if (Layout.ColumnDefinitions[4].Width.Value > 0) layout.RightWidth = Layout.ColumnDefinitions[4].Width.Value;
        if (Layout.RowDefinitions[2].Height.Value > 0) layout.BottomHeight = Layout.RowDefinitions[2].Height.Value;
        layout.IconsVisible = _vm.Panel(PanelIds.Icons).IsVisible;
        layout.OutputVisible = _vm.Panel(PanelIds.Output).IsVisible;
        layout.RightVisible = _vm.RightPanels.Any(p => p.IsVisible);
        try { _vm.Env.SaveSettings(); }
        catch (IOException) { }
        _closeConfirmed = true;
        Close();
    }

    // ------------------------------------------------------------------ IDialogService

    private static IReadOnlyList<FilePickerFileType> ToFileTypes(IReadOnlyList<FileFilter> filters) =>
        filters.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Extensions.Select(e => e == "*" ? "*" : "*." + e).ToList() }).ToList();

    private async Task<IStorageFolder?> FolderAsync(string? dir) =>
        dir != null && Directory.Exists(dir) ? await StorageProvider.TryGetFolderFromPathAsync(dir) : null;

    public async Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters, string? startDirectory = null)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = ToFileTypes(filters),
            SuggestedStartLocation = await FolderAsync(startDirectory),
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string title, IReadOnlyList<FileFilter> filters, string suggestedName, string? startDirectory = null)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = ToFileTypes(filters),
            SuggestedStartLocation = await FolderAsync(startDirectory),
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title, string? startDirectory = null)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await FolderAsync(startDirectory),
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public Task ShowMessageAsync(string text, string title, MessageKind kind = MessageKind.Info) =>
        MessageDialog.ShowAsync(this, text, title, MessageDialog.Buttons.Ok);

    public async Task<ConfirmResult> ConfirmAsync(string text, string title, bool allowCancel) =>
        await MessageDialog.ShowAsync(this, text, title, allowCancel ? MessageDialog.Buttons.YesNoCancel : MessageDialog.Buttons.YesNo);

    public Task RunWithProgressAsync(string title, string tip, Func<CancellationToken, Task> work) =>
        ProgressDialog.RunAsync(this, title, tip, work);

    public Task ShowOptionsAsync(OptionsViewModel options) => new OptionsWindow { DataContext = options }.ShowDialog(this);

    public Task ShowAboutAsync(AboutViewModel about) => new AboutWindow { DataContext = about }.ShowDialog(this);

    public Task ShowDownloadAsync(DownloadViewModel download) => new DownloadWindow { DataContext = download }.ShowDialog(this);
}
