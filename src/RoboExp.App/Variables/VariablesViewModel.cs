using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoboExp.App.Flow;
using RoboExp.App.Localization;
using RoboExp.Core.Model;

namespace RoboExp.App.Variables;

/// <summary>
/// Variables panel (original VariableFrm): local grid = ProjectData.VarList (non-parameter entries), global grid =
/// ProjectInfo.CurUsedGlobalVarList. Edits are validated like the original and applied through
/// <see cref="FlowDocumentViewModel.Edit"/>.
/// </summary>
public sealed partial class VariablesViewModel : ObservableObject
{
    private readonly Func<string, Task> _warn;
    private FlowDocumentViewModel? _doc;
    private int _localCounter, _globalCounter;

    public VariablesViewModel(Func<string, Task> warn) => _warn = warn;

    public ObservableCollection<VariableRowViewModel> Locals { get; } = [];
    public ObservableCollection<VariableRowViewModel> Globals { get; } = [];

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(DeleteLocalCommand))] private VariableRowViewModel? _selectedLocal;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(DeleteGlobalCommand))] private VariableRowViewModel? _selectedGlobal;

    public bool HasDocument => _doc != null;

    public IReadOnlyList<string> Types => _doc == null ? [] : VariableRules.Types(_doc.Session.Type.SoftwareType);

    /// <summary>Global add button tooltip: "Add a global variable" for applications, "Reference a global variable" for subroutines.</summary>
    public string AddGlobalTip => Localizer.Instance.T(_doc?.Session.Data.ProjectInfo?.ProjectType is ProjectType.FlowApp or ProjectType.CodeApp
        ? "Xwb:VariableFrm_AddGlobalVar" : "Xwb:VariableFrm_RefGlobalVar");

    private ProjectData? Data => _doc?.Session.Data;
    private EventArrayList<VarStruct> LocalList => Data!.VarList ??= new();
    private EventArrayList<VarStruct> GlobalList => (Data!.ProjectInfo ??= new()).CurUsedGlobalVarList ??= new();

    /// <summary>SetVariableGrid: binds to the active flow document (null disables both grids); resets the name counters.</summary>
    public void SetDocument(FlowDocumentViewModel? doc)
    {
        if (_doc != null) _doc.ModelChanged -= OnModelChanged;
        _doc = doc;
        if (doc != null) doc.ModelChanged += OnModelChanged;
        _localCounter = _globalCounter = 0;
        Refresh();
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(Types));
        OnPropertyChanged(nameof(AddGlobalTip));
        AddLocalCommand.NotifyCanExecuteChanged();
        AddGlobalCommand.NotifyCanExecuteChanged();
    }

    private void OnModelChanged(object? sender, EventArgs e) => Refresh();

    public void Refresh()
    {
        var selL = SelectedLocal?.Variable;
        var selG = SelectedGlobal?.Variable;
        Locals.Clear();
        Globals.Clear();
        if (Data == null) return;
        foreach (var v in Data.VarList ?? []) if (!v.IsParam) Locals.Add(new VariableRowViewModel(this, v, global: false));
        foreach (var v in Data.ProjectInfo?.CurUsedGlobalVarList ?? []) if (!v.IsParam) Globals.Add(new VariableRowViewModel(this, v, global: true));
        SelectedLocal = Locals.FirstOrDefault(r => r.Variable == selL);
        SelectedGlobal = Globals.FirstOrDefault(r => r.Variable == selG);
    }

    private bool CanAdd() => _doc != null;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddLocal()
    {
        var name = VariableRules.NextName("var", ref _localCounter, LocalList, GlobalList);
        var v = VariableRules.NewVariable(name, _doc!.Session.Type.SoftwareType);
        _doc.Edit(Localizer.Instance.T("Xwb:VariableFrm_AddLocalVar"), () => LocalList.Add(v));
        SelectedLocal = Locals.FirstOrDefault(r => r.Variable == v);
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddGlobal()
    {
        var name = VariableRules.NextName("g_", ref _globalCounter, LocalList, GlobalList);
        var v = VariableRules.NewVariable(name, _doc!.Session.Type.SoftwareType);
        _doc.Edit(Localizer.Instance.T("Xwb:VariableFrm_AddGlobalVar"), () => GlobalList.Add(v));
        SelectedGlobal = Globals.FirstOrDefault(r => r.Variable == v);
    }

    private bool CanDeleteLocal() => SelectedLocal != null;
    private bool CanDeleteGlobal() => SelectedGlobal != null;

    [RelayCommand(CanExecute = nameof(CanDeleteLocal))]
    private void DeleteLocal()
    {
        var v = SelectedLocal?.Variable;
        if (v == null || _doc == null) return;
        _doc.Edit(Localizer.Instance.T("Xwb:VariableFrm_DelVar"), () => LocalList.Remove(v));
    }

    [RelayCommand(CanExecute = nameof(CanDeleteGlobal))]
    private void DeleteGlobal()
    {
        var v = SelectedGlobal?.Variable;
        if (v == null || _doc == null) return;
        _doc.Edit(Localizer.Instance.T("Xwb:VariableFrm_DelVar"), () => GlobalList.Remove(v));
    }

    /// <summary>Adds a variable created by the SetNewVar dialog.</summary>
    public void AddCreated(FlowDocumentViewModel doc, VarStruct v, bool global)
    {
        var data = doc.Session.Data;
        doc.Edit(Localizer.Instance.T("xuwenbin.SetNewVar:$this.Text"), () =>
        {
            if (global) ((data.ProjectInfo ??= new()).CurUsedGlobalVarList ??= new()).Add(v);
            else (data.VarList ??= new()).Add(v);
        });
    }

    internal async Task<bool> RenameAsync(VariableRowViewModel row, string name)
    {
        if (_doc == null) return false;
        var own = (row.IsGlobal ? GlobalList : LocalList).ToList();
        var other = row.IsGlobal ? LocalList : GlobalList;
        var err = VariableRules.CheckName(own, own.IndexOf(row.Variable), other, name,
            row.IsGlobal ? null : _doc.Session.Library.IsSystemFunction);
        if (err != VariableRules.NameError.None)
        {
            await _warn(Localizer.Instance.T(VariableRules.MessageKey(err)));
            return false;
        }
        _doc.Edit(Localizer.Instance.T("Xwb:VariableFrm_Name"), () => row.Variable.Name = name);
        return true;
    }

    internal async Task<bool> SetDefaultAsync(VariableRowViewModel row, string value)
    {
        if (_doc == null) return false;
        if (!VariableRules.IsValidDefault(row.Variable.Type ?? "", value, _doc.Session.Type.SoftwareType))
        {
            await _warn(Localizer.Instance.T("Xwb:VariableFrm_ErrNotMatched"));
            return false;
        }
        _doc.Edit(Localizer.Instance.T("Xwb:VariableFrm_Default"), () => row.Variable.DefaultValue = value);
        return true;
    }

    internal void SetType(VariableRowViewModel row, string type) =>
        _doc?.Edit(Localizer.Instance.T("Xwb:VariableFrm_Type"), () => row.Variable.Type = type);

    internal void SetHint(VariableRowViewModel row, string hint) =>
        _doc?.Edit(Localizer.Instance.T("Xwb:VariableFrm_Hint"), () => row.Variable.Description = hint);
}

/// <summary>One variable: Name/Type/Default/Hint rows of the original property grid category.</summary>
public sealed partial class VariableRowViewModel : ObservableObject
{
    private readonly VariablesViewModel _owner;

    public VariableRowViewModel(VariablesViewModel owner, VarStruct v, bool global)
    {
        _owner = owner;
        Variable = v;
        IsGlobal = global;
        _name = v.Name ?? "";
        _type = v.Type ?? "";
        _defaultValue = v.DefaultValue ?? "";
        _hint = v.Description ?? "";
    }

    public VarStruct Variable { get; }
    public bool IsGlobal { get; }
    public IReadOnlyList<string> Types => _owner.Types;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _type;
    [ObservableProperty] private string _defaultValue;
    [ObservableProperty] private string _hint;

    // Enter commits, and the error box then takes focus (LostFocus commits again): one check at a time.
    private bool _committing;

    public async Task CommitNameAsync()
    {
        if (_committing || Name == Variable.Name) return;
        _committing = true;
        try { if (!await _owner.RenameAsync(this, Name)) Name = Variable.Name ?? ""; }
        finally { _committing = false; }
    }

    public async Task CommitDefaultAsync()
    {
        if (_committing || DefaultValue == Variable.DefaultValue) return;
        _committing = true;
        try { if (!await _owner.SetDefaultAsync(this, DefaultValue)) DefaultValue = Variable.DefaultValue ?? ""; }
        finally { _committing = false; }
    }

    public void CommitHint()
    {
        if (Hint != (Variable.Description ?? "")) _owner.SetHint(this, Hint);
    }

    partial void OnTypeChanged(string value)
    {
        if (!string.IsNullOrEmpty(value) && value != Variable.Type) _owner.SetType(this, value);
    }
}
