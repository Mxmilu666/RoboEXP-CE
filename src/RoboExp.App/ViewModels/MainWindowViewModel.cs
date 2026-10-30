using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoboExp.App.Documents;
using RoboExp.App.Localization;
using RoboExp.App.Palette;
using RoboExp.App.Services;
using RoboExp.Core.Build;
using RoboExp.Core.Install;
using RoboExp.Core.Library;
using RoboExp.Device;

namespace RoboExp.App.ViewModels;

/// <summary>Main window (original MainForm): menus/toolbar commands, document host, tool panels.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private bool _syncingRobotType;

    public MainWindowViewModel(AppEnvironment env, IDialogService dialogs)
    {
        Env = env;
        _dialogs = dialogs;
        Panels =
        [
            new ToolPanel(PanelIds.Icons, "DockFunctionsBox:$this.Text", PanelDock.Left) { Content = Palette },
            new ToolPanel(PanelIds.Properties, "PropertyFrm:$this.Text", PanelDock.Right),
            new ToolPanel(PanelIds.Variables, "VariableWindow", PanelDock.Right),
            new ToolPanel(PanelIds.Hardware, "HardInfoDlg:$this.Text", PanelDock.Right),
            new ToolPanel(PanelIds.Code, "DockCodeBox:$this.Text", PanelDock.Right),
            new ToolPanel(PanelIds.TemplateCode, "DockSysFunc:$this.Text", PanelDock.Right) { Content = TemplateCode },
            new ToolPanel(PanelIds.Output, "DockOutputBox:$this.Text", PanelDock.Bottom) { Content = Output },
        ];
        var layout = env.Settings.Layout;
        Panel(PanelIds.Icons).IsVisible = layout.IconsVisible;
        Panel(PanelIds.Output).IsVisible = layout.OutputVisible;
        foreach (var p in Panels.Where(p => p.Dock == PanelDock.Right)) p.IsVisible = layout.RightVisible;
        _selectedRightPanel = RightPanels.FirstOrDefault();

        Output.NavigateRequested += async (_, row) => await NavigateToAsync(row.File, row.Line ?? 0, row.Column);
        TemplateCode.InsertRequested += (_, t) => InsertTemplate(t);
        env.Changed += (_, _) => OnEnvironmentChanged();
        Documents.CollectionChanged += (_, _) => RefreshCommandStates();
        OnEnvironmentChanged();
        Created?.Invoke(this);
    }

    /// <summary>Raised at the end of construction so feature slices (registered before the window exists) can attach to panels/events.</summary>
    public static event Action<MainWindowViewModel>? Created;

    /// <summary>The dialog service (main window) for feature slices attached via <see cref="Created"/>.</summary>
    public IDialogService Dialogs => _dialogs;

    public AppEnvironment Env { get; }
    public PaletteViewModel Palette { get; } = new();
    public OutputViewModel Output { get; } = new();
    public TemplateCodeViewModel TemplateCode { get; } = new();

    /// <summary>All tool panels; later slices fill <see cref="ToolPanel.Content"/> of Properties/Variables/Hardware/Code.</summary>
    public IReadOnlyList<ToolPanel> Panels { get; }

    public ToolPanel Panel(string id) => Panels.First(p => p.Id == id);
    public IEnumerable<ToolPanel> RightPanels => Panels.Where(p => p.Dock == PanelDock.Right);

    [ObservableProperty]
    private ToolPanel? _selectedRightPanel;

    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    [ObservableProperty]
    private DocumentViewModel? _activeDocument;

    /// <summary>Raised when the active tab changes (original ActiveDocumentChangedHandle) — panels refresh from here.</summary>
    public event EventHandler<DocumentViewModel?>? ActiveDocumentChanged;

    public IReadOnlyList<RobotType> RobotTypes => RobotType.Supported;

    [ObservableProperty]
    private RobotType? _selectedRobotType;

    [ObservableProperty]
    private string _statusText = "";

    public ObservableCollection<string> RecentFiles { get; } = [];

    public bool IsEnglish => Env.Language == UiLanguage.Default;
    public bool IsChs => Env.Language == UiLanguage.CHS;
    public bool IsCht => Env.Language == UiLanguage.CHT;

    public string WindowTitle =>
        Localizer.Instance.T("RoboExp") + " " + Env.RobotType.Name + (ActiveDocument == null ? "" : " - " + ActiveDocument.ToolTip);

    partial void OnActiveDocumentChanged(DocumentViewModel? oldValue, DocumentViewModel? newValue)
    {
        if (oldValue != null) oldValue.PropertyChanged -= OnActiveDocumentPropertyChanged;
        if (newValue != null) newValue.PropertyChanged += OnActiveDocumentPropertyChanged;
        OnPropertyChanged(nameof(WindowTitle));
        RefreshCommandStates();
        ActiveDocumentChanged?.Invoke(this, newValue);
    }

    private void OnActiveDocumentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.FilePath)) OnPropertyChanged(nameof(WindowTitle));
    }

    partial void OnSelectedRobotTypeChanged(RobotType? value)
    {
        if (value != null && !_syncingRobotType) Env.SetRobotType(value);
    }

    /// <summary>LibManager.RootNode: Include.xml merged with UserInclude.xml for the current robot type (palette, user-subroutine drops).</summary>
    public UserLibrary? UserLibrary { get; private set; }

    /// <summary>Replaces the library tree (tests: a UserInclude.xml without touching the per-user cache) and rebuilds the palette.</summary>
    internal void SetUserLibrary(UserLibrary library)
    {
        UserLibrary = library;
        Palette.Refresh(Env.Library, library, Env.Language, null);
    }

    private void OnEnvironmentChanged()
    {
        _syncingRobotType = true;
        SelectedRobotType = Env.RobotType;
        _syncingRobotType = false;
        var libraryError = Env.Library == null && Env.LibraryError != null
            ? (Env.Install == null ? Localizer.Instance.T("Port:InstallMissing") : Localizer.Instance.T("Port:LibraryLoadFailed") + Env.LibraryError)
            : null;
        UserLibrary = Env.Library == null ? null : UserLibrary.Load(Env.Install, Env.Library, Env.Language);
        Palette.Refresh(Env.Library, UserLibrary, Env.Language, libraryError);
        TemplateCode.Refresh(Env.Library, Env.Language);
        StatusText = libraryError ?? "";
        RecentFiles.Clear();
        foreach (var f in Env.Settings.RecentFiles) RecentFiles.Add(f);
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(IsChs));
        OnPropertyChanged(nameof(IsCht));
    }

    private void RefreshCommandStates()
    {
        SaveCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
        SaveAllCommand.NotifyCanExecuteChanged();
        CloseCommand.NotifyCanExecuteChanged();
        CloseAllCommand.NotifyCanExecuteChanged();
        CompileCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        NextWindowCommand.NotifyCanExecuteChanged();
        PreviousWindowCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
    }

    private bool HasActiveDocument() => ActiveDocument != null;
    private bool HasDocuments() => Documents.Count > 0;

    private static IReadOnlyList<FileFilter> FiltersFor(IEnumerable<DocumentKind> kinds)
    {
        var list = kinds.Select(k => new FileFilter(k.FilterName, k.Extensions.Select(e => e.TrimStart('.')).ToList())).ToList();
        list.Add(new FileFilter(Localizer.Instance.T("Port:AllFiles"), ["*"]));
        return list;
    }

    // ---------------------------------------------------------------- File

    /// <summary>
    /// Commands contributed by feature slices (attached via <see cref="Created"/>), bound from menus as
    /// <c>FeatureCommands[Key]</c>. Must be filled before the window binds (i.e. in a <see cref="Created"/> handler).
    /// </summary>
    public Dictionary<string, System.Windows.Input.ICommand> FeatureCommands { get; } = [];

    /// <summary>File → New handler (original FileNewFrm); falls back to an untitled document of the first creatable kind.</summary>
    public Func<Task>? NewHandler { get; set; }

    [RelayCommand]
    private async Task New()
    {
        if (NewHandler != null) { await NewHandler(); return; }
        var kind = DocumentKinds.All.FirstOrDefault(k => k.CreateNew != null);
        if (kind == null) return;
        AddDocument(kind.CreateNew!(Env));
    }

    [RelayCommand]
    private async Task Open()
    {
        var path = await _dialogs.PickOpenFileAsync(Localizer.Instance.T("Open"), FiltersFor(DocumentKinds.All));
        if (path != null) await OpenPathAsync(path);
    }

    /// <summary>File → Open Example Programs: opens a file picker in the robot's vendor Examples directory.</summary>
    [RelayCommand]
    private async Task OpenExample()
    {
        var dir = Env.Install?.ExamplesDirectory(Env.RobotType);
        var path = await _dialogs.PickOpenFileAsync(Localizer.Instance.T("ExamplesDlg:$this.Text"), FiltersFor(DocumentKinds.All),
            dir != null && Directory.Exists(dir) ? dir : null);
        if (path != null) await OpenPathAsync(path);
    }

    [RelayCommand]
    private async Task OpenRecent(string path) => await OpenPathAsync(path);

    [RelayCommand]
    private void ClearRecentFiles()
    {
        Env.Settings.RecentFiles.Clear();
        Env.SaveSettings();
        RecentFiles.Clear();
    }

    /// <summary>Original MainForm.Open(path): activate if already open, else load by kind, add to MRU.</summary>
    public async Task<DocumentViewModel?> OpenPathAsync(string path)
    {
        var loc = Localizer.Instance;
        var existing = Documents.FirstOrDefault(d => d.IsFile(path));
        if (existing != null)
        {
            ActiveDocument = existing;
            return existing;
        }
        if (!File.Exists(path))
        {
            await _dialogs.ShowMessageAsync(loc.T("TheFile") + path + loc.T("NotExists"), loc.T("RoboExp"));
            return null;
        }
        var kind = DocumentKinds.ForPath(path) ?? CodeDocumentViewModel.KindInfo;
        if (kind.ConfirmOpen != null && !await kind.ConfirmOpen(path, Env, _dialogs)) return null;
        DocumentViewModel doc;
        try
        {
            doc = kind.Open(path, Env); // UI thread: document models (TextDocument) have thread affinity
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or NotSupportedException)
        {
            await _dialogs.ShowMessageAsync(loc.T("OpenFileFaild") + "\n" + loc.T("TheFile") + path + "\n" + e.Message, loc.T("RoboExp"), MessageKind.Error);
            return null;
        }
        AddDocument(doc);
        AddRecent(Path.GetFullPath(path));
        return doc;
    }

    public void AddDocument(DocumentViewModel doc)
    {
        Documents.Add(doc);
        ActiveDocument = doc;
    }

    private void AddRecent(string path)
    {
        Env.Settings.AddRecentFile(path);
        Env.SaveSettings();
        RecentFiles.Clear();
        foreach (var f in Env.Settings.RecentFiles) RecentFiles.Add(f);
    }

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private async Task Save() => await SaveDocumentAsync(ActiveDocument!, saveAs: false);

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private async Task SaveAs() => await SaveDocumentAsync(ActiveDocument!, saveAs: true);

    [RelayCommand(CanExecute = nameof(HasDocuments))]
    private async Task SaveAll()
    {
        foreach (var doc in Documents.ToList())
            if (!await SaveDocumentAsync(doc, saveAs: false)) return;
    }

    /// <summary>Saves (prompting for a path when untitled or Save As). Returns false if cancelled or failed.</summary>
    public async Task<bool> SaveDocumentAsync(DocumentViewModel doc, bool saveAs)
    {
        var loc = Localizer.Instance;
        var path = doc.FilePath;
        if (path == null || saveAs)
        {
            path = await _dialogs.PickSaveFileAsync(loc.T("SaveAs"), FiltersFor([doc.Kind]),
                Path.GetFileName(doc.FilePath) ?? "untitled" + doc.Kind.Extensions[0],
                doc.FilePath != null ? Path.GetDirectoryName(doc.FilePath) : null);
            if (path == null) return false;
        }
        try
        {
            await doc.SaveAsync(path);
        }
        catch (EncoderFallbackException)
        {
            await _dialogs.ShowMessageAsync(loc.T("Port:SaveFailed") + loc.T("Port:NotGbk"), loc.T("RoboExp"), MessageKind.Error);
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowMessageAsync(loc.T("Port:SaveFailed") + e.Message, loc.T("RoboExp"), MessageKind.Error);
            return false;
        }
        AddRecent(path);
        OnPropertyChanged(nameof(WindowTitle));
        return true;
    }

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private async Task Close() => await CloseDocumentAsync(ActiveDocument!);

    [RelayCommand(CanExecute = nameof(HasDocuments))]
    private async Task CloseAll() => await CloseAllDocumentsAsync();

    /// <summary>Closes every document, asking about unsaved ones. False if the user cancelled.</summary>
    public async Task<bool> CloseAllDocumentsAsync()
    {
        foreach (var doc in Documents.ToList())
            if (!await CloseDocumentAsync(doc)) return false;
        return true;
    }

    public async Task<bool> CloseDocumentAsync(DocumentViewModel doc)
    {
        if (doc.IsDirty)
        {
            ActiveDocument = doc;
            var loc = Localizer.Instance;
            var answer = await _dialogs.ConfirmAsync(doc.ToolTip + "\n" + loc.T("SaveChange"), loc.T("RoboExp"), allowCancel: true);
            if (answer == ConfirmResult.Cancel) return false;
            if (answer == ConfirmResult.Yes && !await SaveDocumentAsync(doc, saveAs: false)) return false;
        }
        var index = Documents.IndexOf(doc);
        Documents.Remove(doc);
        if (ActiveDocument == doc || ActiveDocument == null)
            ActiveDocument = Documents.Count == 0 ? null : Documents[Math.Min(index, Documents.Count - 1)];
        return true;
    }

    // ---------------------------------------------------------------- Edit

    private bool CanEdit(string? _) => ActiveDocument?.EditTarget != null;

    /// <summary>Edit menu/toolbar/context: routed to the active document's view (original BaseView.Undo/Cut/...).</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task EditAsync(string? action)
    {
        var target = ActiveDocument?.EditTarget;
        if (target == null) return;
        switch (action)
        {
            case "Undo": target.Undo(); break;
            case "Redo": target.Redo(); break;
            case "Cut": await target.CutAsync(); break;
            case "Copy": await target.CopyAsync(); break;
            case "Paste": await target.PasteAsync(); break;
            case "Delete": target.Delete(); break;
            case "SelectAll": target.SelectAll(); break;
            case "Find": target.Find(replace: false); break;
            case "Replace": target.Find(replace: true); break;
        }
    }

    /// <summary>Called by document views once their <see cref="DocumentViewModel.EditTarget"/> is attached.</summary>
    public void NotifyEditTargetChanged() => EditCommand.NotifyCanExecuteChanged();

    // ---------------------------------------------------------------- View / Window

    [RelayCommand]
    private void ShowPanel(string id)
    {
        var panel = Panel(id);
        panel.IsVisible = true;
        if (panel.Dock == PanelDock.Right)
        {
            foreach (var p in RightPanels) p.IsVisible = true;
            SelectedRightPanel = panel;
        }
    }

    [RelayCommand(CanExecute = nameof(HasDocuments))]
    private void NextWindow() => Cycle(+1);

    [RelayCommand(CanExecute = nameof(HasDocuments))]
    private void PreviousWindow() => Cycle(-1);

    private void Cycle(int delta)
    {
        if (Documents.Count == 0) return;
        var i = ActiveDocument == null ? 0 : Documents.IndexOf(ActiveDocument);
        ActiveDocument = Documents[((i + delta) % Documents.Count + Documents.Count) % Documents.Count];
    }

    // ---------------------------------------------------------------- Tool

    [RelayCommand]
    private void SetLanguage(string code) => Env.SetLanguage(AppSettings.ParseLanguage(code));

    [RelayCommand]
    private async Task Options() => await _dialogs.ShowOptionsAsync(new OptionsViewModel(Env, _dialogs));

    [RelayCommand]
    private async Task About() => await _dialogs.ShowAboutAsync(new AboutViewModel(Env));

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private async Task Compile() => await CompileActiveAsync();

    /// <summary>Result of <see cref="CompileActiveAsync"/> when the compiler actually ran.</summary>
    public sealed record CompileOutcome(BuildInput Input, BuildResult Result);

    /// <summary>
    /// Original MainForm.Compile: save, run the compiler behind the modal progress dialog, show the (localized) output.
    /// Returns null when nothing was compiled (no document, no compiler, cancelled).
    /// </summary>
    public async Task<CompileOutcome?> CompileActiveAsync()
    {
        var loc = Localizer.Instance;
        var doc = ActiveDocument;
        if (doc == null) { ShowOutput(loc.T("NoInputFile")); return null; }
        if (doc.FilePath == null && !await SaveDocumentAsync(doc, saveAs: true)) return null;
        BuildInput? input;
        try { input = await doc.PrepareBuildAsync(); }
        catch (BuildRejectedException e) { ShowOutput(e.Message); return null; }
        catch (EncoderFallbackException) { ShowOutput(loc.T("Port:SaveFailed") + loc.T("Port:NotGbk")); return null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShowOutput(loc.T("Port:SaveFailed") + e.Message); return null; }
        if (input == null) { ShowOutput(loc.T("NoInputFile")); return null; }
        if (Env.Install == null) { ShowOutput(loc.T("Port:InstallMissing")); return null; }

        ToolchainLocator toolchain;
        try { toolchain = Env.ResolveToolchain(); }
        catch (DirectoryNotFoundException e) { ShowOutput(loc.T("NoCompiler") + "\n" + e.Message); await Options(); return null; }
        if (!toolchain.IsAvailable) { ShowOutput(loc.T("NoCompiler") + "\n" + loc.T("Port:ToolchainMissing")); await Options(); return null; }

        var builder = new Builder(Env.Install, toolchain) { Language = Env.Language };
        BuildResult? result = null;
        var cancelled = false;
        await _dialogs.RunWithProgressAsync(loc.T("CompileProgressDlg:$this.Text"), loc.T("CompileProgressDlg:lblTip.Text"), async ct =>
        {
            try { result = await builder.BuildAsync(input.SourcePath, Env.RobotType, input.Group, null, ct); }
            catch (OperationCanceledException) { cancelled = true; }
        });
        if (cancelled || result == null) { ShowOutput(loc.T("CancelByUser")); return null; }

        var text = result.LocalizedLog.TrimEnd();
        if (result.Success) text = (text.Length > 0 ? text + "\n" : "") + (input.Warning != null ? input.Warning + "\n" : "") + loc.T("CompilationSuccessful");
        else if (text.Length == 0) text = loc.T("Port:BuildFailed");
        ShowOutput(text);
        return new CompileOutcome(input, result);
    }

    private void ShowOutput(string text)
    {
        Output.SetOutput(text);
        Panel(PanelIds.Output).IsVisible = true;
    }

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private async Task Download()
    {
        var loc = Localizer.Instance;
        var doc = ActiveDocument!;
        if (doc.FilePath == null && !await SaveDocumentAsync(doc, saveAs: true)) return;
        BuildInput? input;
        try { input = await doc.PrepareBuildAsync(); }
        catch (BuildRejectedException e) { ShowOutput(e.Message); return; }
        if (input == null) return;
        if (input.Group == BuildGroup.Func)
        {
            await _dialogs.ShowMessageAsync(loc.T("CannotDownloadSubFile"), loc.T("RoboExp"));
            return;
        }
        var bin = input.BinaryPath;
        if (Env.Settings.CompileBeforeDownload || !File.Exists(bin))
        {
            var compiled = await CompileActiveAsync();
            if (compiled is not { Result.Success: true }) return;
            bin = compiled.Result.BinaryPath ?? bin;
        }
        if (!File.Exists(bin)) return;

        var vm = new DownloadViewModel(bin, Env.RobotType, DownloadMethods.For(Env.RobotType));
        await _dialogs.ShowDownloadAsync(vm);
        if (vm.Outcome is not { } outcome) return;
        var title = loc.T("Download:$this.Text");
        if (outcome.IsSuccess)
        {
            if (Env.Settings.ShowMessageAfterDownload) await _dialogs.ShowMessageAsync(loc.T("DownLoadOK"), title);
        }
        else if (outcome.Status != DownloadStatus.Cancelled)
        {
            await _dialogs.ShowMessageAsync(DownloadViewModel.DescribeOutcome(outcome), loc.T("DownloadFailed"), MessageKind.Warning);
        }
    }

    // ---------------------------------------------------------------- Navigation / templates

    /// <summary>Original DockOutputBox double-click: activate the document owning the file (opening it if needed) and select the line.</summary>
    public async Task NavigateToAsync(string file, int line, int? column)
    {
        if (file.Length == 0) return;
        foreach (var doc in Documents)
        {
            if (doc.TryNavigateTo(file, line, column)) { ActiveDocument = doc; return; }
        }
        if (!File.Exists(file)) return;
        // Original DockOutputBox: a diagnostic in <name>.c opens <name>.rcu when it exists (flow projects map lines to icons).
        var rcu = Path.ChangeExtension(file, ".rcu");
        var opened = await OpenPathAsync(File.Exists(rcu) ? rcu : file);
        opened?.TryNavigateTo(file, line, column);
    }

    private async void InsertTemplate(CodeTemplate template)
    {
        if (ActiveDocument is CodeDocumentViewModel code) code.InsertTemplate(template);
        else await _dialogs.ShowMessageAsync(Localizer.Instance.T("Port:NoActiveCode"), Localizer.Instance.T("RoboExp"));
    }
}
