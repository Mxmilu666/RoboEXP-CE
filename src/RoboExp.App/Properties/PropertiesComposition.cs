using RoboExp.Core.Hardware;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using RoboExp.App.Documents;
using RoboExp.App.Flow;
using RoboExp.App.Hardware;
using RoboExp.App.Localization;
using RoboExp.App.NewProject;
using RoboExp.App.Palette;
using RoboExp.App.Services;
using RoboExp.App.Subroutines;
using RoboExp.App.Variables;
using RoboExp.App.ViewModels;
using RoboExp.App.Views;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;
using RoboExp.Core.Project;
using RoboExp.Core.Rcu;

namespace RoboExp.App.Properties;

/// <summary>
/// Registration and main-window wiring of the property/variable/hardware panels, block editors, new-project and
/// subroutine/library dialogs (original MainForm menu handlers MenuNewSub/MenuRefSub/MenuOpenSub/UpdateSub/
/// MenuManagerSub/MenuProjectProperties/MenuSystemLib and FlowView.UpdateGOBJ).
/// </summary>
public static class PropertiesComposition
{
    private static bool _registered;

    public static MainWindowViewModel? Main { get; private set; }

    /// <summary>The window hosting <see cref="Main"/> (the main window implements its dialog service), else the desktop main window.</summary>
    public static Window? Owner =>
        Main?.Dialogs as Window ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        ViewLocator.Register<BlockPropertiesViewModel>(_ => new BlockPropertiesView());
        ViewLocator.Register<PropertiesPanelViewModel>(_ => new PropertiesPanelView());
        ViewLocator.Register<VariablesViewModel>(_ => new VariablesView());
        HardwareComposition.RegisterViews();
        FlowServices.BlockEditor = new BlockEditorService();
        // LibManager.GetProjectInfo: palette items of UserInclude.xml bands resolve to the library .rcu's ProjectInfo.
        FlowServices.CalleeResolver = (doc, name) =>
            Main?.UserLibrary is { } lib && lib.Type == doc.Session.Type ? lib.GetProjectInfo(name) : null;
        MainWindowViewModel.Created += Attach;
    }

    private static PropertiesPanelViewModel? _properties;
    private static VariablesViewModel? _variables;
    private static HardwarePanelViewModel? _hardware;
    private static readonly Host HostInstance = new();

    private static FlowDocumentViewModel? ActiveFlow => Main?.ActiveDocument as FlowDocumentViewModel;

    private static void Attach(MainWindowViewModel main)
    {
        Main = main;
        _properties = new PropertiesPanelViewModel(HostInstance);
        _variables = new VariablesViewModel(text => HostInstance.ShowWarningAsync(text));
        main.Panel(PanelIds.Properties).Content = _properties;
        main.Panel(PanelIds.Variables).Content = _variables;
        main.ActiveDocumentChanged += (_, doc) => OnActiveDocumentChanged(doc);

        var c = main.FeatureCommands;
        c["NewSub"] = new AsyncRelayCommand(NewSubAsync);
        c["RefSub"] = new AsyncRelayCommand(RefSubAsync);
        c["OpenSub"] = new AsyncRelayCommand(OpenSubAsync);
        c["UpdateSub"] = new AsyncRelayCommand(UpdateSubAsync);
        c["ManagerSub"] = new AsyncRelayCommand(ManagerSubAsync);
        c["ProjectProperties"] = new AsyncRelayCommand(ProjectPropertiesAsync);
        c["SystemLibrary"] = new AsyncRelayCommand(SystemLibraryAsync);
        main.NewHandler = NewProjectAsync;
    }

    private static FlowDocumentViewModel? _modelSource;

    private static void OnActiveDocumentChanged(DocumentViewModel? doc)
    {
        var flow = doc as FlowDocumentViewModel;
        _properties?.SetDocument(flow);
        _variables?.SetDocument(flow);
        if (flow == null) { _hardware = null; Main!.Panel(PanelIds.Hardware).Content = null; }
        else
        {
            _hardware = new HardwarePanelViewModel(() => flow.Session.Data, flow.Session.Type, flow.Session.Install, flow.Edit);
            Main!.Panel(PanelIds.Hardware).Content = _hardware;
        }
        if (_modelSource != null) _modelSource.ModelChanged -= OnModelChanged;
        _modelSource = flow;
        if (flow != null) flow.ModelChanged += OnModelChanged;
        RefreshProjectBand(select: false);
    }

    private static void OnModelChanged(object? sender, EventArgs e)
    {
        RefreshProjectBand(select: false);
        _hardware?.Refresh();
    }

    /// <summary>RefreshOutlookBar: band named after the project with its non-system subroutines.</summary>
    private static void RefreshProjectBand(bool select)
    {
        if (Main == null) return;
        var flow = ActiveFlow;
        if (flow == null) { Main.Palette.SetProjectBand(null); return; }
        var session = flow.Session;
        var items = ((IEnumerable<ProjectInfo>?)session.Data.UsedProjectInfoList ?? [])
            .Where(p => !session.Library.IsSystemFunction(p.ProjectName ?? ""))
            .Select(p => new PaletteItemViewModel(new PaletteDragItem(PaletteItemKind.Function, p.ProjectName ?? ""),
                string.IsNullOrEmpty(p.ProjectCaption) ? p.ProjectName ?? "" : p.ProjectCaption!,
                PaletteViewModel.ProjectToolTip(p), () => PaletteViewModel.DecodeIcon(p.ProjectIcon)))
            .ToList();
        Main.Palette.SetProjectBand(new PaletteBandViewModel(session.Info.ProjectName ?? "", items), select);
    }

    private static Task Message(string text, MessageKind kind = MessageKind.Warning) =>
        Main?.Dialogs.ShowMessageAsync(text, Localizer.Instance.T("RoboExp"), kind) ?? Task.CompletedTask;

    // ------------------------------------------------------------------ File → New / Project → New Subroutine

    /// <summary>MainForm.NewFile: FileNewFrm, ProjectSession.CreateNew, open; subroutines open ProjectInfoFrm.</summary>
    private static async Task NewProjectAsync()
    {
        if (Main == null || Owner is not { } owner) return;
        if (Main.Env.Install is not { } install) { await Message(Localizer.Instance.T("Port:InstallMissing")); return; }
        var dir = NewProjectRules.DefaultProjectsDirectory(Main.Env.RobotType); // created with the project
        var lib = Main.Env.Library;
        var dlg = new NewProjectWindow(dir, null, n => lib?.IsSystemFunction(n) == true, Main.Dialogs);
        await dlg.ShowDialog(owner);
        if (dlg.Result is not { } req) return;
        await CreateAndOpenAsync(req, install);
    }

    private static async Task<ProjectSession?> CreateAndOpenAsync(NewProjectRequest req, Core.Install.VendorInstall install)
    {
        var main = Main!;
        if (main.Documents.FirstOrDefault(d => d.IsFile(req.RcuPath)) is { } open)
        {
            main.ActiveDocument = open;
            await Message(Localizer.Instance.T("TheFile") + req.RcuPath + Localizer.Instance.T("HasAlreadyOpened"), MessageKind.Info);
            return null;
        }
        ProjectSession session;
        try
        {
            session = ProjectSession.CreateNew(req.RcuPath, req.Kind, main.Env.RobotType, install, main.Env.Language,
                projectIcon: DefaultProjectIcon.Get(), startIcon: FlowImages.EncodeControlIcon(IconType.Start), description: req.Description);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await Message(Localizer.Instance.T("Port:SaveFailed") + e.Message, MessageKind.Error);
            return null;
        }
        if (NewProjectRules.IsSubroutine(req.Kind)) await EditProjectInfoAsync(session, null);
        await main.OpenPathAsync(req.RcuPath);
        return session;
    }

    private static async Task NewSubAsync()
    {
        if (Main == null || Owner is not { } owner || ActiveFlow is not { } parent) return;
        var install = parent.Session.Install;
        var lib = parent.Session.Library;
        var dlg = new NewProjectWindow(parent.Session.ProjectDirectory, parent.Session.ProjectDirectory, lib.IsSystemFunction, Main.Dialogs);
        await dlg.ShowDialog(owner);
        if (dlg.Result is not { } req) return;
        var sub = await CreateAndOpenAsync(req, install);
        if (sub == null) return;
        var info = RcuReader.Open(sub.RcuPath).ProjectData!.ProjectInfo!;
        parent.Edit(Localizer.Instance.T("NewSub"), () => parent.Session.RefProjectInfo(info));
        Main.ActiveDocument = parent;
        RefreshProjectBand(select: true);
    }

    /// <summary>MenuRefSub (SubroutineDialogs.ReferenceSubAsync: pick a FlowFunc/CodeFunc .rcu and reference it).</summary>
    private static async Task RefSubAsync()
    {
        if (Owner is not { } owner || ActiveFlow is not { } doc) return;
        if (await SubroutineDialogs.ReferenceSubAsync(owner, doc.Session))
        {
            doc.Edit(Localizer.Instance.T("RefSub"), () => { });
            RefreshProjectBand(select: true);
        }
    }

    /// <summary>OpenSelectedFuncIcon: opens the selected user-subroutine icon's .rcu from the project directory.</summary>
    private static async Task OpenSubAsync()
    {
        if (Main == null || ActiveFlow is not { } doc) return;
        if (doc.Selection is not { Count: 1 } sel || sel[0].DataObject is not FuncData f || string.IsNullOrEmpty(f.ProjectName)) return;
        if (doc.Session.Library.IsSystemFunction(f.ProjectName)) return;
        var path = Path.Combine(doc.Session.ProjectDirectory, f.ProjectName + ".rcu");
        await Main.OpenPathAsync(path);
    }

    private static async Task UpdateSubAsync()
    {
        if (Owner is not { } owner || ActiveFlow is not { } doc) return;
        if (await SubroutineDialogs.ShowUpdateSubAsync(owner, doc.Session, Main?.UserLibrary)) AfterProjectDataChanged(doc, "UpdateSub");
    }

    private static async Task ManagerSubAsync()
    {
        if (Main == null || Owner is not { } owner || ActiveFlow is not { } doc) return;
        var changed = await SubroutineDialogs.ShowSubManagerAsync(owner, doc.Session,
            async p => await Main.OpenPathAsync(p),
            info => doc.Session.DrawingObjects?.List?.Any(g => g.DataObject is FuncData f && f.ProjectName == info.ProjectName) == true);
        if (changed) AfterProjectDataChanged(doc, "ManagerSub");
    }

    /// <summary>Dialogs that mutate ProjectData directly: record as one undoable edit and refresh.</summary>
    private static void AfterProjectDataChanged(FlowDocumentViewModel doc, string key)
    {
        doc.Edit(Localizer.Instance.T(key), () => { });
        RefreshProjectBand(select: false);
    }

    private static async Task ProjectPropertiesAsync()
    {
        if (ActiveFlow is not { } doc) return;
        await EditProjectInfoAsync(doc.Session, doc);
    }

    /// <summary>ProjectInfoFrm on <paramref name="session"/>; applied through <paramref name="doc"/>.Edit when open, else saved.</summary>
    private static async Task EditProjectInfoAsync(ProjectSession session, FlowDocumentViewModel? doc)
    {
        if (Owner is not { } owner) return;
        var hw = (IReadOnlyList<HardwareItem>?)session.Data.HardwareTable?.HardwareItemList?.ToList() ?? [];
        var dlg = new ProjectInfoWindow(session.Info, hw, session.Type.SoftwareType);
        await dlg.ShowDialog(owner);
        if (!dlg.Accepted) return;
        if (doc != null) doc.Edit(Localizer.Instance.T("ProjectProperties"), () => dlg.ApplyTo(doc.Session.Info));
        else
        {
            dlg.ApplyTo(session.Info);
            session.Save();
        }
    }

    private static async Task SystemLibraryAsync()
    {
        if (Main == null || Owner is not { } owner || Main.Env.Install is not { } install) return;
        if (await SubroutineDialogs.ShowSystemLibraryAsync(owner, install, Main.Env.RobotType)) Main.Env.Reload();
    }

    // ------------------------------------------------------------------ block editor

    /// <summary>
    /// Canvas double-click / Enter / "Properties": X3/X4 use the docked PropertyFrm (shown and focused); the other
    /// controllers get the floating PropertyFrmNew.
    /// </summary>
    private sealed class BlockEditorService : IBlockEditorService
    {
        public async Task<bool> EditAsync(FlowDocumentViewModel doc, GraphicObject target)
        {
            if (BlockPropertiesViewModel.KindOf(target) == BlockEditorKind.None || Main == null) return false;
            if (UsesDockedEditor(doc.Session.Type.SoftwareType))
            {
                _properties?.Show(doc, target);
                Main.ShowPanelCommand.Execute(PanelIds.Properties);
                return true;
            }
            if (Owner is not { } owner) return false;
            var vm = new BlockPropertiesViewModel(doc, target, docked: false, HostInstance);
            await new BlockEditorWindow(vm).ShowDialog(owner);
            return true;
        }
    }

    /// <summary>DesignSurface MouseUp: the popup PropertyFrmNew is used for every type except X3RCU/X4RCU.</summary>
    public static bool UsesDockedEditor(SoftwareType t) => t is SoftwareType.X3RCU or SoftwareType.X4RCU;

    private sealed class Host : IBlockEditorHost
    {
        private static Window? Top => Owner?.OwnedWindows.LastOrDefault(w => w.IsActive) ?? Owner;

        public async Task ShowWarningAsync(string text)
        {
            if (Top is { } w) await MessageDialog.ShowAsync(w, text, Localizer.Instance.T("Xwb:RobotExpress"), MessageDialog.Buttons.Ok);
        }

        public async Task ShowInfoAsync(string text)
        {
            if (Top is { } w) await MessageDialog.ShowAsync(w, text, Localizer.Instance.T("Xwb:RobotExpress"), MessageDialog.Buttons.Ok);
        }

        public void ShowVariables() => Main?.ShowPanelCommand.Execute(PanelIds.Variables);

        public async Task<string?> DefineNewVariableAsync(FlowDocumentViewModel doc)
        {
            if (Top is not { } w) return null;
            var data = doc.Session.Data;
            var dlg = new NewVariableWindow(((IEnumerable<VarStruct>?)data.VarList ?? []).ToList(),
                ((IEnumerable<VarStruct>?)data.ProjectInfo?.CurUsedGlobalVarList ?? []).ToList(), doc.Session.Library.IsSystemFunction);
            await dlg.ShowDialog(w);
            switch (dlg.Result)
            {
                case NewVariableWindow.Outcome.Created:
                    _variables?.AddCreated(doc, dlg.Variable!, dlg.IsGlobal);
                    return dlg.Variable!.Name;
                case NewVariableWindow.Outcome.More:
                    ShowVariables();
                    return null;
                default:
                    return null;
            }
        }

        public Task<string?> DefineHardwareInstanceAsync(FlowDocumentViewModel doc, string hardwareType) =>
            DefineHardwareInstanceCoreAsync(Top, doc, hardwareType, null);
    }

    /// <summary>
    /// MainForm.Compile pre-check (X3/X4, "instance undefined"): opens HardwareInstFrm for <paramref name="typeName"/> prefilled
    /// with <paramref name="instanceName"/> and, on OK, adds the instance to the project table (system types are merged in
    /// like the original combined table) as one undoable edit. Returns false when cancelled or unavailable.
    /// </summary>
    public static async Task<bool> DefineHardwareInstanceAsync(FlowDocumentViewModel doc, string typeName, string instanceName) =>
        await DefineHardwareInstanceCoreAsync(Owner?.OwnedWindows.LastOrDefault(w => w.IsActive) ?? Owner, doc, typeName, instanceName) != null;

    private static async Task<string?> DefineHardwareInstanceCoreAsync(Window? owner, FlowDocumentViewModel doc, string typeName, string? suggestedName)
    {
        if (owner == null) return null;
        var inst = await HardwareDialogs.ShowInstanceDialogAsync(owner, doc.Session.Data, doc.Session.Type, doc.Session.Install, typeName, suggestedName);
        if (inst == null) return null;
        var system = HardwareCatalog.Get(doc.Session.Install, doc.Session.Type).SystemTable;
        var data = doc.Session.Data;
        doc.Edit(Localizer.Instance.T("Xwb:DefineHardwareInfo"), () => HardwareTables.AddInstance(system, data.HardwareTable ??= new(), inst));
        _hardware?.Refresh();
        return inst.Name;
    }
}
