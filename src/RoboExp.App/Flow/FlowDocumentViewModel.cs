using Avalonia.Input.Platform;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using RoboExp.App.Documents;
using RoboExp.App.Flow.Editing;
using RoboExp.App.Localization;
using RoboExp.App.Palette;
using RoboExp.App.Services;
using RoboExp.Core.Build;
using RoboExp.Core.CodeGen;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;
using RoboExp.Core.Project;
using RoboExp.Core.Rcu;

namespace RoboExp.App.Flow;

/// <summary>Original <c>ToolType</c> of the design surface.</summary>
public enum FlowTool { AutoTool, Select, Connect, Scroll, Text }

/// <summary>
/// A flowchart project tab (original FlowView + the model side of DesignSurface): project session, selection, snapshot
/// undo, the editing commands of the surface and toolbar, save/compile/navigation. The canvas
/// (<see cref="FlowCanvas"/>) renders it and turns pointer input into these calls.
/// </summary>
public sealed partial class FlowDocumentViewModel : DocumentViewModel, IEditCommands
{
    /// <summary>".rcu" projects; code projects (CodeApp/CodeFunc) open as <see cref="CodeProjectDocumentViewModel"/>.</summary>
    public static DocumentKind KindInfo { get; } = new("rcu", "RCU File", [".rcu"], OpenAny, ConfirmOpen: ConfirmOpenAsync);

    private readonly List<GraphicObject> _selection = [];

    public FlowDocumentViewModel(ProjectSession session, AppEnvironment env)
    {
        Session = session;
        Env = env;
        Config = FlowViewConfig.Load(env.Install, session.Type);
        Undo = new FlowUndoManager(CaptureState, RestoreState) { MaxCount = Config.MaxUndoSteps };
        FilePath = session.RcuPath;
        EditTarget = this;
        Session.Regenerate();
    }

    /// <summary>
    /// MainForm.Open: the project is opened for the current robot type (the original's global SoftwareType); a project saved
    /// for another type has its ProjectInfo.SoftwareType switched to the current one after opening.
    /// </summary>
    public static DocumentViewModel OpenAny(string path, AppEnvironment env)
    {
        var install = env.Install ?? throw new IOException(Localizer.Instance.T("Port:InstallMissing"));
        var session = ProjectSession.Open(path, install, env.RobotType);
        if (session.Data.ProjectInfo is { } info && info.SoftwareType != env.RobotType.SoftwareType) info.SoftwareType = env.RobotType.SoftwareType;
        return session.IsFlow ? new FlowDocumentViewModel(session, env) : new CodeProjectDocumentViewModel(session);
    }

    /// <summary>
    /// MainForm.Open (2603-2626): when the stored SoftwareType differs from the current robot type, ask
    /// CWRM "OpenDiffSoftTypeWarning" (Yes/No; No cancels), then tell the user to redefine the ports (CWRM "RedefinePort").
    /// </summary>
    public static async Task<bool> ConfirmOpenAsync(string path, AppEnvironment env, IDialogService dialogs)
    {
        SoftwareType? stored;
        try { stored = RcuReader.Read(path).ProjectData?.ProjectInfo?.SoftwareType; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or NotSupportedException)
        {
            return true; // the open itself reports the error (CWRM "OpenFileFaild")
        }
        if (stored == null || stored == env.RobotType.SoftwareType) return true;
        var loc = Localizer.Instance;
        if (await dialogs.ConfirmAsync(loc.T("OpenDiffSoftTypeWarning"), loc.T("RoboExp"), allowCancel: false) == ConfirmResult.No) return false;
        await dialogs.ShowMessageAsync(loc.T("RedefinePort"), loc.T("RoboExp"));
        return true;
    }

    public override DocumentKind Kind => KindInfo;

    public ProjectSession Session { get; }
    public AppEnvironment Env { get; }
    public FlowViewConfig Config { get; }
    public FlowUndoManager Undo { get; }

    internal Func<IClipboard?>? ClipboardProvider { get; set; }

    /// <summary>The wire being drawn; shared with auto layout, whose default routing honours its IsHorizontal toggle.</summary>
    public ConnectingLines Wire { get; } = new();

    public GraphicObjectCollection DrawingObjects => Session.DrawingObjects!;
    public IReadOnlyList<GraphicObject> Objects => (IReadOnlyList<GraphicObject>?)Session.DrawingObjects?.List ?? [];

    public IReadOnlyList<GraphicObject> Selection => _selection;

    public event EventHandler? SelectionChanged;

    /// <summary>Raised after every model change (edit, undo, redo, open): repaint, regenerate panels.</summary>
    public event EventHandler? ModelChanged;

    /// <summary>Asks the view to show a message (original MessageBox.Show).</summary>
    public event EventHandler<string>? MessageRequested;

    /// <summary>Asks the view to bring an object into view (DesignSurface.ScrollObjectIntoView).</summary>
    public event EventHandler<GraphicObject>? ScrollIntoViewRequested;

    /// <summary>Raised when code lines should be highlighted in the code window (DockCodeBox.SetSelectLines).</summary>
    public event EventHandler<(int Begin, int End)>? CodeLinesSelected;

    [ObservableProperty]
    private FlowTool _tool;

    public bool IsAutoTool => Tool == FlowTool.AutoTool;
    public bool IsSelectTool => Tool == FlowTool.Select;
    public bool IsConnectTool => Tool == FlowTool.Connect;
    public bool IsScrollTool => Tool == FlowTool.Scroll;
    public bool IsTextTool => Tool == FlowTool.Text;

    partial void OnToolChanged(FlowTool value)
    {
        Wire.Reset();
        ClearSelection();
        OnPropertyChanged(nameof(IsAutoTool));
        OnPropertyChanged(nameof(IsSelectTool));
        OnPropertyChanged(nameof(IsConnectTool));
        OnPropertyChanged(nameof(IsScrollTool));
        OnPropertyChanged(nameof(IsTextTool));
    }

    /// <summary>DesignSurfaceData.ScrollOffset (persisted with the project, like the original).</summary>
    public Point ScrollOffset
    {
        get => Session.File.FlowData?.DesignSurfaceData?.ScrollOffset ?? Point.Empty;
        set
        {
            if (Session.File.FlowData?.DesignSurfaceData is { } d) d.ScrollOffset = value;
        }
    }

    /// <summary>World point of the last mouse-down (DesignSurface.m_LastMouseDownPos), the paste anchor.</summary>
    public Point LastMouseDownPos { get; set; }

    /// <summary>Size of the visible canvas in pixels, maintained by the canvas.</summary>
    public Size ViewSize { get; set; }

    /// <summary>Visible world rectangle (paste centre): the scroll offset plus <see cref="ViewSize"/>.</summary>
    public Rect32 ViewRect => new(ScrollOffset.X, ScrollOffset.Y, ViewSize.Width, ViewSize.Height);

    /// <summary>The generated &lt;name&gt;.c text of the last regeneration.</summary>
    public string Code => Session.LastGeneration?.Text ?? Session.Data.CodeFrameData?.AllCode ?? "";

    // ------------------------------------------------------------------ editing core

    private FlowEditor NewEditor()
    {
        var editor = new FlowEditor(DrawingObjects.List!, _selection);
        editor.StartLimitReached += (_, _) => MessageRequested?.Invoke(this, "最多8个");
        return editor;
    }

    /// <summary>
    /// Records an undo snapshot, applies <paramref name="mutate"/>, regenerates the code and notifies the views. Use for
    /// every model change made by panels/dialogs too, so it is undoable and repainted.
    /// </summary>
    public void Edit(string description, Action mutate)
    {
        var selectionBefore = _selection.Count;
        if (Undo.Record(description, mutate)) IsDirty = true;
        var live = new HashSet<GraphicObject>(Objects, ReferenceEqualityComparer.Instance);
        _selection.RemoveAll(go => !live.Contains(go));
        AfterModelChange();
        if (selectionBefore != _selection.Count) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EditWithEditor(string description, Action<FlowEditor> mutate) => Edit(description, () => mutate(NewEditor()));

    private void AfterModelChange()
    {
        Session.Regenerate();
        ModelChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Repaint/regenerate after a change that is not an undoable edit (e.g. tool or port size).</summary>
    public void NotifyViewChanged() => ModelChanged?.Invoke(this, EventArgs.Empty);

    private byte[] CaptureState()
    {
        var ms = new MemoryStream();
        Session.WriteRcu(ms);
        return ms.ToArray();
    }

    private void RestoreState(byte[] state)
    {
        var scroll = ScrollOffset;
        Session.Restore(RcuReader.Read(new MemoryStream(state)));
        ScrollOffset = scroll;
    }

    // ------------------------------------------------------------------ selection

    public void SetSelection(IEnumerable<GraphicObject> objects)
    {
        var list = objects.Distinct().ToList();
        if (list.SequenceEqual(_selection)) return;
        _selection.Clear();
        _selection.AddRange(list);
        OnSelectionChanged();
    }

    public void ClearSelection() => SetSelection([]);

    public void AddToSelection(GraphicObject go)
    {
        if (_selection.Contains(go)) return;
        _selection.Add(go);
        OnSelectionChanged();
    }

    public void RemoveFromSelection(GraphicObject go)
    {
        if (_selection.Remove(go)) OnSelectionChanged();
    }

    /// <summary>FlowView.SelectedObjectChangedHandle → UpdateGOBJ: highlight the selected icon's code lines.</summary>
    private void OnSelectionChanged()
    {
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        if (_selection.Count == 0 || _selection[0].DrawObject is not IconDraw || Session.Graph == null) return;
        var state = Session.Graph.State(_selection[0]);
        if (state.BeginLine != -1) CodeLinesSelected?.Invoke(this, (state.BeginLine, state.EndLine != -1 ? state.EndLine : state.BeginLine));
    }

    /// <summary>FlowView.ScrollObjectIntoView without changing the selection (e.g. ScrollStartIconIntoView after open).</summary>
    public void Reveal(GraphicObject go) => ScrollIntoViewRequested?.Invoke(this, go);

    /// <summary>FlowView.SetSelectedObject.</summary>
    public void SelectAndReveal(GraphicObject go)
    {
        if (!Objects.Contains(go)) return;
        SetSelection([go]);
        ScrollIntoViewRequested?.Invoke(this, go);
    }

    /// <summary>FlowView.SelectLine: select the object generating line <paramref name="line"/> of the .c file.</summary>
    public void SelectLine(int line)
    {
        CodeLinesSelected?.Invoke(this, (line, line));
        if (Session.Graph?.FindObjectAtLine(line) is { } go) SelectAndReveal(go);
    }

    // ------------------------------------------------------------------ surface operations

    /// <summary>DesignSurface drag end (MoveOffset + UpdateTwoSidePos/UpdateConnectLinesPos + CombineLines).</summary>
    public void MoveSelection(int dx, int dy)
    {
        if (_selection.Count == 0) return;
        EditWithEditor("Move", e => e.MoveSelection(dx, dy));
    }

    /// <summary>DesignSurface.CopyAfterDragDrop: Ctrl+drag duplicates the selection (needs at least one non-line).</summary>
    public void CopySelectionWithOffset(int dx, int dy)
    {
        if (!_selection.Any(go => go.DrawObject is not LineDraw)) return;
        var clones = FlowClipboard.Clone(_selection.ToList(), UsedInfosFor(_selection), out var used);
        EditWithEditor("Copy", e =>
        {
            e.AddClones(clones, dx, dy);
            e.RemoveInvalidateLines();
            RegisterUsed(clones, used);
        });
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>DesignSurface.AddConnectingLines (finishing a wire).</summary>
    public void FinishWire()
    {
        if (Wire.StartPort == null || Wire.EndPort == null)
        {
            Wire.Reset();
            return;
        }
        EditWithEditor("Connect", e => e.AddConnectingLines(Wire));
        Wire.Reset();
    }

    /// <summary>F5: DesignSurface removes wires not attached at both ends.</summary>
    public void RemoveDanglingWires() => EditWithEditor("Remove", e => e.RemoveInvalidateLines());

    /// <summary>Text tool: a TextDraw at <paramref name="pos"/>.</summary>
    public void AddText(Point pos, string text)
    {
        if (text.Length == 0) return;
        Edit("Add", () =>
        {
            var go = FlowFactory.NewText(pos, text);
            NewEditor().AddGraphicObject(go);
        });
    }

    public void Align(FlowEditor.Align align) => EditWithEditor("Align", e => e.AlignIcons(align));

    /// <summary>FlowView.AutoAlignIcon (AutoArrayGraphicObjects.Refresh), then scroll to the Start icon.</summary>
    public void AutoAlign()
    {
        GraphicObject? start = null;
        EditWithEditor("AutoAlign", e => start = new AutoArrange(e, Wire).Refresh());
        Wire.Reset();
        if (start != null) ScrollIntoViewRequested?.Invoke(this, start);
    }

    /// <summary>
    /// FlowView.AddIcon (+ AddPairEndIcon): drop of a palette item at world point <paramref name="pos"/> (icon centred).
    /// Refused while 8 Start icons exist. Function icons register the callee in UsedProjectInfoList.
    /// </summary>
    public GraphicObject? AddIcon(PaletteDragItem item, Point pos)
    {
        if (Objects.Count(o => o.DrawObject is IconDraw { IconType: IconType.Start }) >= 8)
        {
            MessageRequested?.Invoke(this, "最多8个");
            return null;
        }
        var topLeft = new Point(pos.X - FlowGeometry.IconWidth / 2, pos.Y - FlowGeometry.IconHeight / 2);
        GraphicObject? added = null;
        if (item.Kind == PaletteItemKind.Control)
        {
            if (!Enum.TryParse<IconType>(item.Name, out var type)) return null;
            added = AddControlIcon(type, topLeft);
            if (added != null && FlowFactory.PairEndType(type) is { } end)
                AddControlIcon(end, new Point(pos.X + FlowGeometry.IconWidth * 3 - FlowGeometry.IconWidth / 2, topLeft.Y));
        }
        else
        {
            var source = ResolveCallee(item.Name);
            Edit("Add", () =>
            {
                var info = Session.AddUsedProjectInfo(item.Name, source);
                if (info?.ProjectIcon == null) return;
                var go = FlowFactory.NewIcon(IconType.Function, topLeft, info.ProjectIcon, FuncCallText.CreateFuncData(info));
                if (NewEditor().AddGraphicObject(go)) added = go;
                UpdateUsedHardwareInstance();
            });
        }
        return added;
    }

    private GraphicObject? AddControlIcon(IconType type, Point topLeft)
    {
        GraphicObject? added = null;
        var image = FlowImages.ControlIconImage(type, Objects);
        if (image == null) return null;
        Edit("Add", () =>
        {
            var go = FlowFactory.NewIcon(type, topLeft, image, FlowFactory.NewControlData(type));
            if (NewEditor().AddGraphicObject(go)) added = go;
            UpdateUsedHardwareInstance();
        });
        return added;
    }

    /// <summary>
    /// The signature a "func:" palette item calls: an already used signature, else the system library entry
    /// (LibManager.CreateLibProjInfo), else nothing (project subroutines are registered in UsedProjectInfoList by
    /// New/Ref Subroutine before they can be dropped).
    /// </summary>
    private ProjectInfo? ResolveCallee(string name)
    {
        if (Session.Data.GetUsedProjectInfo(name) is { } used) return used;
        if (Session.Library.IsSystemFunction(name) && Session.Library.Find(name) is { } f && Session.Install is { } install)
            return LibraryProjectInfo.FromLibFunction(f, install, Session.Type, Env.Language);
        if (FlowServices.CalleeResolver?.Invoke(this, name) is { } user) return user;
        return null;
    }

    private void UpdateUsedHardwareInstance()
    {
        Session.Regenerate();
        if (Session.LastGeneration != null) PreCompileChecks.UpdateUsedHardwareInstance(Session.Data, Session.LastGeneration.UsedFunctionIcons);
    }

    private IEnumerable<ProjectInfo> UsedInfosFor(IEnumerable<GraphicObject> objects) =>
        objects.Select(o => o.DataObject).OfType<FuncData>()
            .Select(f => Session.Data.GetUsedProjectInfo(f.ProjectName ?? ""))
            .OfType<ProjectInfo>()
            .Distinct();

    /// <summary>FlowView.AddUsedToProjectInfoList for pasted Function icons.</summary>
    private void RegisterUsed(IEnumerable<GraphicObject> objects, IReadOnlyList<ProjectInfo> carried)
    {
        foreach (var f in objects.Select(o => o.DataObject).OfType<FuncData>())
        {
            var name = f.ProjectName ?? "";
            if (Session.Data.GetUsedProjectInfo(name) != null) continue;
            var source = carried.FirstOrDefault(i => i.ProjectName == name) ?? ResolveCallee(name);
            Session.AddUsedProjectInfo(name, source);
        }
    }

    /// <summary>FlowView.RemoveNoUsedSysFromUsedProjectInfoList: forget system functions no icon calls any more.</summary>
    private void RemoveUnusedSystemInfos()
    {
        var list = Session.Data.UsedProjectInfoList;
        if (list == null) return;
        var called = Objects.Select(o => o.DataObject).OfType<FuncData>().Select(f => f.ProjectName).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < list.Count; i++)
        {
            var name = list[i].ProjectName ?? "";
            if (!called.Contains(name) && Session.Library.IsSystemFunction(name)) list.RemoveAt(i--);
        }
    }

    // ------------------------------------------------------------------ IEditCommands (Edit menu, toolbar, keys)

    void IEditCommands.Undo() => DoUndo();
    void IEditCommands.Redo() => DoRedo();

    /// <summary>DesignSurface.Undo: restore, clear the selection, repaint.</summary>
    public void DoUndo()
    {
        if (!Undo.Undo()) return;
        IsDirty = true;
        _selection.Clear();
        AfterModelChange();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DoRedo()
    {
        if (!Undo.Redo()) return;
        IsDirty = true;
        _selection.Clear();
        AfterModelChange();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task CutAsync()
    {
        var selected = _selection.ToArray();
        if (!await CopySelectionAsync()) return;
        // Clipboard access is asynchronous: remove only the objects that were actually copied.
        SetSelection(selected.Where(Objects.Contains));
        Delete();
    }

    public async Task CopyAsync() => await CopySelectionAsync();

    private async Task<bool> CopySelectionAsync()
    {
        if (_selection.Count == 0 || ClipboardProvider?.Invoke() is not { } clipboard) return false;
        try
        {
            await FlowClipboard.SetAsync(clipboard, _selection.ToList(), UsedInfosFor(_selection));
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or COMException or UnauthorizedAccessException)
        {
            MessageRequested?.Invoke(this, e.Message);
            return false;
        }
    }

    /// <summary>Paste original or CE clipboard blocks at the last mouse-down position or view centre.</summary>
    public async Task PasteAsync()
    {
        if (ClipboardProvider?.Invoke() is not { } clipboard) return;
        List<GraphicObject> clones;
        List<ProjectInfo> used;
        try
        {
            if (await FlowClipboard.GetAsync(clipboard) is not { } selection) return;
            (clones, used) = selection;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or COMException or UnauthorizedAccessException)
        {
            MessageRequested?.Invoke(this, e.Message);
            return;
        }
        if (clones.Count == 0) return;
        var target = LastMouseDownPos != Point.Empty && ViewRect.Contains(LastMouseDownPos)
            ? LastMouseDownPos
            : new Point(ViewRect.X + ViewRect.Width / 2, ViewRect.Y + ViewRect.Height / 2);
        var center = FlowEditor.GetCenterPoint(clones);
        if (center == Point.Empty) center = target;
        EditWithEditor("Paste", e =>
        {
            e.AddClones(clones, target.X - center.X, target.Y - center.Y);
            RegisterUsed(clones, used);
        });
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Delete()
    {
        if (_selection.Count == 0) return;
        EditWithEditor("Delete", e =>
        {
            e.DeleteSelection();
            RemoveUnusedSystemInfos();
        });
        _selection.Clear();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectAll() => SetSelection(Objects);

    public void Find(bool replace)
    {
    }

    // ------------------------------------------------------------------ document

    /// <summary>FlowView.Save/SaveAs: UpdateProjectDataBeforeSave, .rcu, &lt;name&gt;.c, HardwareInfo.c.</summary>
    public override Task SaveAsync(string path)
    {
        RemoveUnusedSystemInfos();
        RegisterUsed(Objects, []);
        if (string.Equals(Path.GetFullPath(path), Session.RcuPath, PathComparison)) Session.Save();
        else Session.SaveAs(path);
        FilePath = Session.RcuPath;
        IsDirty = false;
        ModelChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    /// <summary>
    /// MainForm.Compile for a flow project: pre-checks (param check aborts, return check warns; X3/X4 hardware check
    /// aborts), save, then compile &lt;name&gt;.c with group main (apps) or func (subroutines).
    /// </summary>
    public override async Task<BuildInput?> PrepareBuildAsync()
    {
        var report = Session.RunPreCompileChecks();
        var loc = Localizer.Instance;
        string? warning = null;
        foreach (var issue in report.Issues)
        {
            if (issue.Object != null) SelectAndReveal(issue.Object);
            var text = issue.Kind switch
            {
                PreCompileIssueKind.TooFewArgumentsToSubroutine => DiagnosticLine(issue.Line, "error", loc.T("TooFewArgumentsToSubroutine"), issue.Name),
                PreCompileIssueKind.ReturnVarNotSet => DiagnosticLine(issue.Line, "warning", loc.T("ReturnVarNotSet"), issue.Name),
                _ => loc.T("Port") + " " + issue.Name + " " + loc.T("NotDefine"),
            };
            if (issue.Kind == PreCompileIssueKind.HardwareInstanceNotDefined
                && PreCompileChecks.CheckHardInstHasDefine(Session.Data) is { TypeName: { } typeName } missing)
            {
                // MainForm.Compile: HardwareInstFrm for the undefined instance; compilation stops either way.
                await Properties.PropertiesComposition.DefineHardwareInstanceAsync(this, typeName, missing.Name ?? issue.Name);
            }
            if (issue.IsError) throw new BuildRejectedException(text);
            warning ??= text;
        }
        await SaveAsync(Session.RcuPath);
        var bin = Path.Combine(Session.ProjectDirectory, Session.Info.ProjectName + ".bin");
        return new BuildInput(Session.CPath, Session.IsApp ? BuildGroup.Main : BuildGroup.Func, bin) { Warning = warning };
    }

    /// <summary>MainForm.Compile pre-check line: <c>&lt;dir&gt;\&lt;Name&gt;.c:&lt;line&gt;: error:&lt;text&gt; "&lt;func&gt;"</c>.</summary>
    private string DiagnosticLine(int line, string severity, string text, string name) =>
        Session.ProjectDirectory + Path.DirectorySeparatorChar + Session.Info.ProjectName + ".c:" + line + ": " + severity + ":" + text + " \"" + name + "\"";

    /// <summary>Diagnostics in &lt;name&gt;.c (or the .rcu itself) select the object generating that line.</summary>
    public override bool TryNavigateTo(string file, int line, int? column)
    {
        string full;
        try { full = Path.GetFullPath(file.Replace('\\', Path.DirectorySeparatorChar), Session.ProjectDirectory); }
        catch (ArgumentException) { return false; }
        if (!string.Equals(full, Session.CPath, PathComparison) && !string.Equals(full, Session.RcuPath, PathComparison)) return false;
        SelectLine(line);
        return true;
    }
}
