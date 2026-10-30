using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoboExp.App.Localization;
using RoboExp.Core.Hardware;
using RoboExp.Core.Install;
using RoboExp.Core.Model;

namespace RoboExp.App.Hardware;

/// <summary>A row of the hardware tree: a type (with its instances as children) or an instance.</summary>
public abstract class HardwareNode
{
    public abstract string Caption { get; }
    public virtual IReadOnlyList<HardwareNode> Children => [];
}

public sealed class HardwareTypeNode(HardwareItem item, bool isSystem) : HardwareNode
{
    public HardwareItem Item { get; } = item;
    public HardwareType Type => Item.HardwareType!;
    public string Name => Type.Name ?? "";

    /// <summary>Defined by the controller's system table (cannot be edited or deleted).</summary>
    public bool IsSystem { get; } = isSystem;

    /// <summary>UpdateInfo: "Name (NickName)".</summary>
    public override string Caption => string.IsNullOrEmpty(Type.NickName) ? Name : $"{Name} ({Type.NickName})";

    public List<HardwareInstanceNode> Instances { get; } = [];
    public override IReadOnlyList<HardwareNode> Children => Instances;
}

public sealed class HardwareInstanceNode(HardwareInstance instance, HardwareTypeNode parent) : HardwareNode
{
    public HardwareInstance Instance { get; } = instance;
    public HardwareTypeNode Parent { get; } = parent;
    public string Name => Instance.Name ?? "";
    public override string Caption => Name;
}

/// <summary>A row of the "Subroutine using" tree; undefined entries are highlighted and can be defined by double-click.</summary>
public sealed class UsedHardwareNode(string caption, string typeName, string? instanceName, bool isDefined, IReadOnlyList<HardwareNode>? children = null) : HardwareNode
{
    public override string Caption { get; } = caption;
    public string TypeName { get; } = typeName;

    /// <summary>Null for a type row.</summary>
    public string? InstanceName { get; } = instanceName;
    public bool IsUndefined { get; } = !isDefined;
    public override IReadOnlyList<HardwareNode> Children { get; } = children ?? [];
}

/// <summary>
/// The docked Hardware pane (original HardInfoDlg tab of the active project): the project's hardware table combined
/// with the controller's system table, the instances its subroutines use, the selected type's description/pins and
/// the selected instance's pins on the controller picture. Changes go through <c>edit(description, mutate)</c> so the
/// host can snapshot for undo, regenerate and mark the project dirty; the pane refreshes itself afterwards.
/// </summary>
public sealed partial class HardwarePanelViewModel : ObservableObject
{
    private readonly Func<ProjectData?> _project;
    private readonly RobotType _type;
    private readonly VendorInstall? _install;
    private readonly Action<string, Action> _edit;
    private HardwareCatalog? _catalog;
    private HardwareTable _combined = new();

    /// <param name="project">The active flow project's data, or null when none is active.</param>
    /// <param name="edit">Runs a model mutation as one undoable, dirty-marking change (FlowDocumentViewModel.Edit).</param>
    public HardwarePanelViewModel(Func<ProjectData?> project, RobotType type, VendorInstall? install, Action<string, Action> edit)
    {
        _project = project;
        _type = type;
        _install = install;
        _edit = edit;
        Localizer.Instance.LanguageChanged += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>Owner window for the dialogs; set by the view when attached.</summary>
    public Func<Window?>? OwnerProvider { get; set; }

    public ObservableCollection<HardwareTypeNode> Types { get; } = [];
    public ObservableCollection<UsedHardwareNode> UsedBySubroutines { get; } = [];

    [ObservableProperty] private string? _placeholder;
    [ObservableProperty] private bool _hasUsage;
    [ObservableProperty] private Bitmap? _picture;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddInstanceCommand))]
    private RcuBoard? _board;
    [ObservableProperty] private IReadOnlyList<PinMark> _marks = [];
    [ObservableProperty] private string _typeDescription = "";
    [ObservableProperty] private IReadOnlyList<string> _pinDescriptions = [];
    [ObservableProperty] private bool _isDefaultOrder;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddInstanceCommand), nameof(EditCommand), nameof(DeleteCommand))]
    private HardwareNode? _selectedNode;

    public bool HasContent => Placeholder == null;

    partial void OnPlaceholderChanged(string? value)
    {
        OnPropertyChanged(nameof(HasContent));
        AddTypeCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedNodeChanged(HardwareNode? value)
    {
        var typeNode = value switch { HardwareTypeNode t => t, HardwareInstanceNode i => i.Parent, _ => null };
        TypeDescription = typeNode?.Type.Description ?? "";
        PinDescriptions = typeNode == null
            ? []
            : Enumerable.Range(0, typeNode.Type.PinCount)
                .Select(i => $"Pin{i}: {(typeNode.Type.PinDescriptionList is { } d && i < d.Count ? d[i] : "")}").ToList();
        IsDefaultOrder = typeNode?.Type.IsDefault ?? false;
        var marks = new List<PinMark>();
        if (value is HardwareInstanceNode { Instance.PinTeam: { } team })
        {
            foreach (var n in team.CtrlPinNumList ?? []) marks.Add(new PinMark(PinKind.Ctrl, n, PinMarkStyle.Selected));
            foreach (var n in team.GNDPinNumList ?? []) marks.Add(new PinMark(PinKind.Gnd, n, PinMarkStyle.Selected));
            foreach (var n in team.VccPinNumList ?? []) marks.Add(new PinMark(PinKind.Vcc, n, PinMarkStyle.Selected));
        }
        Marks = marks;
    }

    /// <summary>Rebuilds the pane from the active project (call after undo/redo or when the active document changes).</summary>
    public void Refresh()
    {
        var keep = SelectedNode switch
        {
            HardwareTypeNode t => (t.Name, (string?)null),
            HardwareInstanceNode i => (i.Parent.Name, i.Name),
            _ => ((string?)null, (string?)null),
        };
        Types.Clear();
        UsedBySubroutines.Clear();
        SelectedNode = null;
        HasUsage = false;

        var data = _project();
        var loc = Localizer.Instance;
        if (data == null)
        {
            Placeholder = loc.T("Port:Hw.NoProject");
            return;
        }
        if (_install == null)
        {
            Placeholder = loc.T("Port:InstallMissing");
            return;
        }
        _catalog = HardwareCatalog.Get(_install, _type);
        Board = _catalog.Board;
        Picture = _catalog.Picture;
        if (_catalog.SystemTable == null && (data.HardwareTable?.HardwareItemList?.Count ?? 0) == 0)
        {
            Placeholder = string.Format(loc.T("Port:Hw.NoTable"), _type.Name);
            return;
        }
        Placeholder = null;

        _combined = HardwareTables.GetCombinedTable(_catalog.SystemTable, data.HardwareTable);
        HardwareNode? reselect = null;
        // HardInfoDlg sorts its tree view (TreeView.Sorted).
        foreach (var item in (_combined.HardwareItemList ?? []).Where(i => i.HardwareType != null)
                     .OrderBy(i => i.HardwareType!.Name, StringComparer.Ordinal))
        {
            var node = new HardwareTypeNode(item, HardwareTables.ContainsType(_catalog.SystemTable, item.HardwareType!.Name));
            foreach (var inst in (item.InstanceList ?? []).OrderBy(i => i.Name, StringComparer.Ordinal))
            {
                var child = new HardwareInstanceNode(inst, node);
                node.Instances.Add(child);
                if (keep.Item2 != null && child.Name == keep.Item2) reselect = child;
            }
            if (keep.Item2 == null && keep.Item1 != null && node.Name == keep.Item1) reselect = node;
            Types.Add(node);
        }

        foreach (var used in HardwareTables.GetSubroutineUsage(data.ProjectInfo, _combined))
        {
            var children = used.Instances.Select(i => (HardwareNode)new UsedHardwareNode(i.Name, used.TypeName, i.Name, i.IsDefined)).ToList();
            var caption = string.IsNullOrEmpty(used.NickName) ? used.TypeName : $"{used.TypeName} ({used.NickName})";
            UsedBySubroutines.Add(new UsedHardwareNode(caption, used.TypeName, null, used.IsDefined, children));
        }
        HasUsage = UsedBySubroutines.Count > 0;
        SelectedNode = reselect;
    }

    private bool CanAddInstance() => SelectedNode is HardwareTypeNode or HardwareInstanceNode && Board != null;

    /// <summary>btnAddInstance: a new instance of the selected type, prefilled with an undefined name its subroutines use.</summary>
    [RelayCommand(CanExecute = nameof(CanAddInstance))]
    private async Task AddInstanceAsync()
    {
        var typeNode = SelectedNode switch { HardwareTypeNode t => t, HardwareInstanceNode i => i.Parent, _ => null };
        if (typeNode == null) return;
        var suggested = UsedBySubroutines.FirstOrDefault(u => u.TypeName == typeNode.Name)?.Children
            .OfType<UsedHardwareNode>().LastOrDefault(c => c.IsUndefined)?.InstanceName;
        await AddInstanceAsync(typeNode.Name, suggested);
    }

    /// <summary>"Subroutine using" double-click: define an undefined instance, or edit a defined one.</summary>
    public async Task ActivateUsedAsync(UsedHardwareNode node)
    {
        if (node.InstanceName == null || !HardwareTables.ContainsType(_combined, node.TypeName)) return;
        var target = Types.SelectMany(t => t.Instances).FirstOrDefault(i => i.Name == node.InstanceName);
        if (target != null)
        {
            SelectedNode = target;
            await EditAsync();
        }
        else
        {
            SelectedNode = Types.FirstOrDefault(t => t.Name == node.TypeName);
            await AddInstanceAsync(node.TypeName, node.InstanceName);
        }
    }

    /// <summary>"Subroutine using" selection: select the matching instance, else its type (SelectPortNodeInLeftTreeView).</summary>
    public void SelectUsed(UsedHardwareNode node)
    {
        HardwareNode? target = node.InstanceName == null
            ? null
            : Types.SelectMany(t => t.Instances).FirstOrDefault(i => i.Name == node.InstanceName);
        target ??= Types.FirstOrDefault(t => t.Name == node.TypeName);
        if (target != null) SelectedNode = target;
    }

    private async Task AddInstanceAsync(string typeName, string? suggestedName)
    {
        if (OwnerProvider?.Invoke() is not { } owner || _project() is not { } data || _install == null) return;
        var inst = await HardwareDialogs.ShowInstanceDialogAsync(owner, data, _type, _install, typeName, suggestedName);
        if (inst == null) return;
        Apply("HardInfoDlg:btnAddInstance.Text", t => HardwareTables.AddInstance(_catalog?.SystemTable, t, inst));
        SelectedNode = Types.SelectMany(t => t.Instances).FirstOrDefault(i => i.Name == inst.Name) ?? SelectedNode;
    }

    private bool CanAddType() => _project() != null && _install != null && Placeholder == null;

    /// <summary>btnAddType: HardwareTypeFrm for a new project-defined type.</summary>
    [RelayCommand(CanExecute = nameof(CanAddType))]
    private async Task AddTypeAsync()
    {
        if (OwnerProvider?.Invoke() is not { } owner || _project() is not { } data || _install == null) return;
        var type = await HardwareDialogs.ShowTypeDialogAsync(owner, data, _type, _install);
        if (type == null) return;
        Apply("HardInfoDlg:btnAddType.Text", t => (t.HardwareItemList ??= new()).Add(new HardwareItem { HardwareType = type, InstanceList = new() }));
        SelectedNode = Types.FirstOrDefault(n => n.Name == type.Name) ?? SelectedNode;
    }

    /// <summary>treeViewLeft_AfterSelect: instances and project-defined types can be edited/deleted, system types cannot.</summary>
    private bool CanEditOrDelete() => SelectedNode is HardwareInstanceNode || SelectedNode is HardwareTypeNode { IsSystem: false };

    /// <summary>btnEdit: HardwareTypeFrm for a project type, HardwareInstFrm (pins only) for an instance.</summary>
    [RelayCommand(CanExecute = nameof(CanEditOrDelete))]
    private async Task EditAsync()
    {
        if (OwnerProvider?.Invoke() is not { } owner || _project() is not { } data || _install == null) return;
        switch (SelectedNode)
        {
            case HardwareTypeNode { IsSystem: false } typeNode:
            {
                var edited = await HardwareDialogs.ShowTypeDialogAsync(owner, data, _type, _install, typeNode.Name);
                if (edited == null) return;
                Apply("HardInfoDlg:btnEdit.Text", t =>
                {
                    if (HardwareTables.FindItem(t, typeNode.Name)?.HardwareType is not { } target) return;
                    target.NickName = edited.NickName;
                    target.Description = edited.Description;
                    target.IsDefault = edited.IsDefault;
                    target.PinDescriptionList = edited.PinDescriptionList;
                    target.CtrlLimitList = edited.CtrlLimitList;
                    target.GNDLimitList = edited.GNDLimitList;
                    target.VccLimitList = edited.VccLimitList;
                });
                break;
            }
            case HardwareInstanceNode instNode:
            {
                var pins = await HardwareDialogs.ShowEditInstanceDialogAsync(owner, data, _type, _install, instNode.Name);
                if (pins == null) return;
                Apply("HardInfoDlg:btnEdit.Text", t => HardwareTables.SetInstancePins(_catalog?.SystemTable, t, instNode.Name, pins));
                break;
            }
        }
    }

    /// <summary>btnDel: deletes the selected instance or project-defined type (with its instances).</summary>
    [RelayCommand(CanExecute = nameof(CanEditOrDelete))]
    private void Delete()
    {
        switch (SelectedNode)
        {
            case HardwareTypeNode { IsSystem: false } typeNode:
                Apply("HardInfoDlg:btnDel.Text", t => HardwareTables.RemoveType(_catalog?.SystemTable, t, typeNode.Name));
                break;
            case HardwareInstanceNode instNode:
            {
                var siblings = instNode.Parent.Instances;
                var index = siblings.IndexOf(instNode);
                var next = index + 1 < siblings.Count ? siblings[index + 1].Name : index > 0 ? siblings[index - 1].Name : null;
                var typeName = instNode.Parent.Name;
                Apply("HardInfoDlg:btnDel.Text", t => HardwareTables.RemoveInstance(_catalog?.SystemTable, t, instNode.Name));
                // btnDel_Click: select the next (else previous) instance, else the type.
                SelectedNode = (HardwareNode?)Types.SelectMany(t => t.Instances).FirstOrDefault(i => i.Name == next)
                    ?? Types.FirstOrDefault(t => t.Name == typeName);
                break;
            }
        }
    }

    private void Apply(string descriptionKey, Action<HardwareTable> mutate)
    {
        _edit(Localizer.Instance.T(descriptionKey), () =>
        {
            if (_project() is not { } data) return;
            data.HardwareTable ??= new HardwareTable { SoftwareType = _type.SoftwareType };
            mutate(data.HardwareTable);
        });
        Refresh();
    }
}
