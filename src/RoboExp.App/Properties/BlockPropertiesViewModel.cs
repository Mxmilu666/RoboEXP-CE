using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoboExp.App.Flow;
using RoboExp.App.Localization;
using RoboExp.Core.CodeGen;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;

namespace RoboExp.App.Properties;

/// <summary>Which editor panel the selection uses (original FlowView.UpdateGOBJ dispatch / SetTypeVisible).</summary>
public enum BlockEditorKind { None, Function, Expression, For, CodeEditor, Default, Text }

/// <summary>Callbacks into the UI (message boxes, variable pane, new-variable and hardware-instance dialogs).</summary>
public interface IBlockEditorHost
{
    Task ShowWarningAsync(string text);
    Task ShowInfoAsync(string text);
    void ShowVariables();
    /// <summary>SetNewVar: returns the created variable name or null.</summary>
    Task<string?> DefineNewVariableAsync(FlowDocumentViewModel doc);
    /// <summary>HardwareInstFrm for <paramref name="hardwareType"/>: returns the created instance name or null.</summary>
    Task<string?> DefineHardwareInstanceAsync(FlowDocumentViewModel doc, string hardwareType);
}

/// <summary>
/// Property editor of one flowchart object: the content of the docked PropertyFrm (X3/X4) and the popup PropertyFrmNew.
/// Every change goes through <see cref="FlowDocumentViewModel.Edit"/> (undo snapshot, code regeneration, dirty).
/// </summary>
public sealed partial class BlockPropertiesViewModel : ObservableObject
{
    private readonly IBlockEditorHost _host;
    private bool _loading;

    public BlockPropertiesViewModel(FlowDocumentViewModel doc, GraphicObject target, bool docked, IBlockEditorHost host)
    {
        Doc = doc;
        Target = target;
        Docked = docked;
        _host = host;
        Kind = KindOf(target);
        Reload();
    }

    public FlowDocumentViewModel Doc { get; }
    public GraphicObject Target { get; }
    /// <summary>Docked PropertyFrm (X3/X4 port lists) vs popup PropertyFrmNew.</summary>
    public bool Docked { get; }
    public BlockEditorKind Kind { get; }

    private ProjectData Data => Doc.Session.Data;
    private SoftwareType Software => Doc.Session.Type.SoftwareType;
    private DataObject? DataObj => Target.DataObject;

    public static BlockEditorKind KindOf(GraphicObject? go) => go?.DrawObject switch
    {
        null => BlockEditorKind.None,
        LineDraw => BlockEditorKind.None,
        TextDraw => BlockEditorKind.Text,
        _ => go.DataObject switch
        {
            FuncData => BlockEditorKind.Function,
            BeginIfData or BeginWhileData or CalculateData => BlockEditorKind.Expression,
            ReturnData => BlockEditorKind.Expression,
            BeginForData => BlockEditorKind.For,
            CodeEditorData => BlockEditorKind.CodeEditor,
            _ => BlockEditorKind.Default,
        },
    };

    public bool IsFunction => Kind == BlockEditorKind.Function;
    public bool IsExpression => Kind == BlockEditorKind.Expression && ExpressionEditable;
    public bool IsFor => Kind == BlockEditorKind.For;
    public bool IsCodeEditor => Kind == BlockEditorKind.CodeEditor;
    public bool IsText => Kind == BlockEditorKind.Text;
    /// <summary>Hint (Description) row: everything except text annotations (which edit their text instead).</summary>
    public bool HasHint => Kind is not (BlockEditorKind.None or BlockEditorKind.Text);

    /// <summary>ReturnData uses the expression editor only when the project returns something (else SetDefaultProperty).</summary>
    private bool ExpressionEditable => DataObj is not ReturnData || Data.ProjectInfo?.ReturnType?.Type is not (null or "" or "void");

    /// <summary>CbbEqual is hidden for Calculate (it is an assignment).</summary>
    public bool ShowEqualSign => DataObj is not CalculateData;

    public string Title => Kind switch
    {
        BlockEditorKind.Function => (DataObj?.ProjectName ?? "") + " - " + Localizer.Instance.T("PropertyFrm:$this.Text"),
        _ => Localizer.Instance.T("PropertyFrm:$this.Text"),
    };

    // ------------------------------------------------------------- common

    [ObservableProperty] private string _hint = "";
    [ObservableProperty] private string _text = "";

    [ObservableProperty] private string _helpName = "";
    [ObservableProperty] private string _helpDescription = "";
    private int? _helpParameterIndex;

    /// <summary>Selection help is independent of the editable, user-authored Hint.</summary>
    public void ShowHelp(string name, string description)
    {
        _helpParameterIndex = null;
        HelpName = name;
        HelpDescription = description;
    }

    public void ShowParameterHelp(ParameterRowViewModel row)
    {
        ShowHelp(row.Label, row.Description);
        _helpParameterIndex = row.Index;
    }

    public void ShowParameterHelp(string name)
    {
        if (Parameters.FirstOrDefault(p => p.Parameter == Param(name)) is { } row) ShowParameterHelp(row);
    }

    internal string ParameterDescription(ParameterClass parameter)
    {
        var library = Doc.Session.Library.Find(FunctionName)?.Params.FirstOrDefault(p => p.Name == parameter.Name);
        var description = library?.Description.Get(Localizer.Instance.Language);
        return ParameterRowViewModel.DescriptionFor(parameter,
            string.IsNullOrWhiteSpace(description) ? parameter.Description : description);
    }

    /// <summary>Reads the model into the editor fields (also after undo/redo).</summary>
    public void Reload()
    {
        _loading = true;
        try
        {
            Hint = DataObj?.GetDescription() ?? "";
            if (Target.DrawObject is TextDraw td) Text = td.Text ?? "";
            switch (DataObj)
            {
                case FuncData f: LoadFunction(f); break;
                case BeginIfData d: Expression = d.Expression ?? ""; break;
                case BeginWhileData d: Expression = d.Expression ?? ""; break;
                case CalculateData d: Expression = d.Expression ?? ""; break;
                case ReturnData d: Expression = d.Expression ?? ""; break;
                case BeginForData d: LoopCount = d.LoopCountStr ?? ""; break;
                case CodeEditorData d:
                    Codes = string.Concat((IEnumerable<string?>?)d.Codes ?? []);
                    GlobalCode = Data.CodeFrameData?.Global ?? "";
                    break;
            }
            VariableCandidates = BuildVariableList(includeDefine: true);
            if (_helpParameterIndex is { } index && index < Parameters.Count)
                ShowParameterHelp(Parameters[index]);
            else
                ShowHelp(IsFunction ? SubName : Title,
                    IsFunction && !string.IsNullOrWhiteSpace(SubDescription) ? SubDescription
                    : Localizer.Instance.T(IsFunction ? "Xwb:PropertyFrm_InfoSubDesc" : "Xwb:PropertyFrm_InfoHint"));
        }
        finally { _loading = false; }
    }

    /// <summary>Original EnableList: non-hardware params, VarList, CurUsedGlobalVarList, then "DefineVar".</summary>
    public IReadOnlyList<string> BuildVariableList(bool includeDefine)
    {
        var list = new List<string>();
        foreach (var p in Data.ProjectInfo?.ParamList ?? []) if (!p.IsHardwareType) list.Add(p.Name ?? "");
        foreach (var v in Data.VarList ?? []) list.Add(v.Name ?? "");
        foreach (var v in Data.ProjectInfo?.CurUsedGlobalVarList ?? []) list.Add(v.Name ?? "");
        if (includeDefine) list.Add(DefineVarText);
        return list;
    }

    public static string DefineVarText => Localizer.Instance.T("Tzw:DefineVar");

    [ObservableProperty] private IReadOnlyList<string> _variableCandidates = [];

    /// <summary>Description lines (original string[] editor): CRLF/LF split, empty → null.</summary>
    internal static string?[]? SplitLines(string text)
    {
        if (text.Length == 0) return null;
        return text.Replace("\r\n", "\n").Split('\n');
    }

    public void CommitHint()
    {
        if (_loading || DataObj == null || Hint == DataObj.GetDescription()) return;
        var lines = SplitLines(Hint);
        Doc.Edit(Localizer.Instance.T("Xwb:PropertyFrm_Hint"), () => DataObj.Description = lines);
    }

    public void CommitText()
    {
        if (_loading || Target.DrawObject is not TextDraw td || td.Text == Text) return;
        var text = Text;
        Doc.Edit(Localizer.Instance.T("Xwb:PropertyFrm_Hint"), () => td.Text = text);
    }

    // ------------------------------------------------------------- function

    public ObservableCollection<ParameterRowViewModel> Parameters { get; } = [];
    public ProjectInfo? Callee { get; private set; }
    public string SubName => Callee?.ProjectName ?? DataObj?.ProjectName ?? "";
    public string SubDescription => FunctionDescription;
    public string ReturnTypeText => Callee?.ReturnType?.Type ?? "";
    /// <summary>The Return category exists only for non-void callees.</summary>
    public bool HasReturn => Callee?.ReturnType?.Type is not (null or "" or "void");
    public bool HasCallee => Callee != null;

    [ObservableProperty] private string _code = "";
    [ObservableProperty] private string _returnVariable = "";

    private void LoadFunction(FuncData f)
    {
        Callee = f.ProjectName == null ? null : Data.GetUsedProjectInfo(f.ProjectName);
        Code = f.Code ?? "";
        ReturnVariable = f.ReturnName ?? "";
        Parameters.Clear();
        var ps = Callee?.ParamList ?? [];
        for (var i = 0; i < ps.Count; i++)
        {
            var value = f.ValueList != null && i < f.ValueList.Count ? f.ValueList[i] ?? "" : "";
            Parameters.Add(new ParameterRowViewModel(this, i, ps[i], value, CandidatesFor(ps[i])));
        }
        OnPropertyChanged(nameof(SubName));
        OnPropertyChanged(nameof(SubDescription));
        OnPropertyChanged(nameof(ReturnTypeText));
        OnPropertyChanged(nameof(HasReturn));
        OnPropertyChanged(nameof(HasCallee));
    }

    /// <summary>Hardware/enum params: fixed list (ComboBoxDisable); base types: variables + DefineVar (ComboBoxEnable).</summary>
    internal IReadOnlyList<string> CandidatesFor(ParameterClass p)
    {
        if (!p.IsHardwareType && !p.IsEnumType) return BuildVariableList(includeDefine: true);
        var fixedList = HardwarePorts.FixedCandidates(Software, Callee?.ProjectName, p, Docked);
        if (fixedList != null) return fixedList;
        return [.. HardwarePorts.TableInstances(Data.HardwareTable, p), DefineHardwareText];
    }

    public static string DefineHardwareText => Localizer.Instance.T(HardwarePorts.DefineHardwareInfoKey);

    private bool IsKnownName(string name) =>
        (Data.VarList ?? []).Any(v => v.Name == name)
        || (Data.ProjectInfo?.CurUsedGlobalVarList ?? []).Any(v => v.Name == name)
        || (Data.ProjectInfo?.ParamList ?? []).Any(p => p.Name == name);

    /// <summary>
    /// Original m_propertyBag_SetValue for a parameter row. Returns false (and reverts the row) when rejected.
    /// </summary>
    internal async Task<bool> SetParameterAsync(ParameterRowViewModel row, string value)
    {
        if (_loading || DataObj is not FuncData f) return true;
        var p = row.Parameter;
        if (ValueCheck.IsBaseType(p.Type))
        {
            if (string.Equals(value, DefineVarText, StringComparison.OrdinalIgnoreCase))
            {
                var created = await _host.DefineNewVariableAsync(Doc);
                if (created == null) { _host.ShowVariables(); return false; }
                value = created;
            }
            var err = ValueCheck.CheckParameter(p, value, IsKnownName, Software);
            if (err != ValueCheck.ParamError.None)
            {
                await _host.ShowWarningAsync(Localizer.Instance.T(ValueCheck.ErrorKey(err)));
                return false;
            }
        }
        else if (p.IsHardwareType && string.Equals(value, DefineHardwareText, StringComparison.OrdinalIgnoreCase))
        {
            var created = await _host.DefineHardwareInstanceAsync(Doc, p.Type ?? "");
            if (created == null) return false;
            value = created;
        }
        if (f.ValueList != null && row.Index < f.ValueList.Count && f.ValueList[row.Index] == value) return true;
        var callee = Callee;
        var index = row.Index;
        Doc.Edit(row.Label, () =>
        {
            f.ValueList ??= new ArrayList<string?>();
            while (f.ValueList.Count <= index) f.ValueList.Add("");
            f.ValueList[index] = value;
            FuncCallText.SetCodeStr(f, callee);
        });
        Reload();
        return true;
    }

    partial void OnReturnVariableChanged(string value)
    {
        if (_loading) return;
        _ = SetReturnVariableAsync(value);
    }

    private async Task SetReturnVariableAsync(string value)
    {
        if (DataObj is not FuncData f) return;
        if (string.Equals(value, DefineVarText, StringComparison.OrdinalIgnoreCase))
        {
            var created = await _host.DefineNewVariableAsync(Doc);
            if (created == null) { _host.ShowVariables(); Reload(); return; }
            value = created;
        }
        if (f.ReturnName == value) return;
        var callee = Callee;
        Doc.Edit(Localizer.Instance.T("Xwb:PropertyFrm_ReturnVariable"), () =>
        {
            f.ReturnName = value;
            FuncCallText.SetCodeStr(f, callee);
        });
        Reload();
    }

    // ------------------------------------------------------------- expression

    [ObservableProperty] private string _expression = "";

    /// <summary>Operator buttons of the sign bar (Tag text inserted at the caret), with their tooltip keys.</summary>
    public IReadOnlyList<SignButton> Signs { get; } =
    [
        new("&&", "Xwb:ExpressionFrm_LogicalAnd"), new("||", "Xwb:ExpressionFrm_LogicalOr"), new("!", "Xwb:ExpressionFrm_LogicalNot"),
        new("<", "Xwb:ExpressionFrm_Lesser"), new("<=", "Xwb:ExpressionFrm_LesserEqual"), new(">", "Xwb:ExpressionFrm_Greater"),
        new(">=", "Xwb:ExpressionFrm_GreaterEqual"), new("==", "Xwb:ExpressionFrm_Equal"), new("!=", "Xwb:ExpressionFrm_NotEqual"),
        new("+", "Xwb:ExpressionFrm_Addition"), new("-", "Xwb:ExpressionFrm_Subtraction"), new("*", "Xwb:ExpressionFrm_Multiplication"),
        new("/", "Xwb:ExpressionFrm_Division"), new("%", "Xwb:ExpressionFrm_Modulo"), new("=", "Xwb:ExpressionFrm_Assignment"),
        new("(", "Xwb:ExpressionFrm_LeftParenthesis"), new(")", "Xwb:ExpressionFrm_RightParenthesis"), new(" ", "Xwb:ExpressionFrm_Space"),
        new("0", null), new("1", null), new("2", null), new("3", null), new("4", null),
        new("5", null), new("6", null), new("7", null), new("8", null), new("9", null),
    ];

    /// <summary>Asks the view to insert text at the caret (AddSign); the view then calls <see cref="CommitExpression"/>.</summary>
    public event EventHandler<string>? InsertRequested;

    /// <summary>Raised to select the offending range after a failed check (TxtExprFocus).</summary>
    public event EventHandler<(int Start, int Length)>? SelectRequested;

    [RelayCommand]
    private void InsertSign(string sign) => InsertRequested?.Invoke(this, sign);

    /// <summary>CmbVariable: a variable inserts its name; "DefineVar" opens SetNewVar and inserts the new name.</summary>
    public async Task PickVariableAsync(string item)
    {
        if (string.Equals(item, DefineVarText, StringComparison.OrdinalIgnoreCase))
        {
            var created = await _host.DefineNewVariableAsync(Doc);
            if (created == null) return;
            VariableCandidates = BuildVariableList(includeDefine: true);
            item = created;
        }
        InsertRequested?.Invoke(this, item);
    }

    /// <summary>Writes the expression (the original writes on every keystroke; the port commits on focus loss/insert/check/close).</summary>
    public void CommitExpression()
    {
        if (_loading) return;
        var text = Expression;
        switch (DataObj)
        {
            case BeginIfData d when d.Expression != text: Doc.Edit(Localizer.Instance.T("Xwb:ExpressionFrm_Expression"), () => d.Expression = text); break;
            case BeginWhileData d when d.Expression != text: Doc.Edit(Localizer.Instance.T("Xwb:ExpressionFrm_Expression"), () => d.Expression = text); break;
            case CalculateData d when d.Expression != text: Doc.Edit(Localizer.Instance.T("Xwb:ExpressionFrm_Expression"), () => d.Expression = text); break;
            case ReturnData d when d.Expression != text: Doc.Edit(Localizer.Instance.T("Xwb:ExpressionFrm_Expression"), () => d.Expression = text); break;
        }
    }

    /// <summary>BtnCheck / Enter: advisory CheckExpression; invalid text is still stored.</summary>
    [RelayCommand]
    private async Task CheckExpression()
    {
        CommitExpression();
        var r = ExpressionChecker.Check(Expression);
        if (r.IsValid) { await _host.ShowInfoAsync(Localizer.Instance.T("Xwb:ExpressionFrm_RightExpr")); return; }
        SelectRequested?.Invoke(this, (r.Start, r.Length));
        await _host.ShowWarningAsync(Localizer.Instance.T(ExpressionChecker.MessageKey(r.Error)));
    }

    // ------------------------------------------------------------- for

    [ObservableProperty] private string _loopCount = "";

    partial void OnLoopCountChanged(string value)
    {
        if (_loading || DataObj is not BeginForData d) return;
        if (string.Equals(value, DefineVarText, StringComparison.OrdinalIgnoreCase))
        {
            _host.ShowVariables();
            _loading = true;
            LoopCount = d.LoopCountStr ?? "";
            _loading = false;
            return;
        }
        if (d.LoopCountStr == value) return;
        Doc.Edit(Localizer.Instance.T("Xwb:PropertyFrm_LoopTimes"), () => d.LoopCountStr = value);
    }

    // ------------------------------------------------------------- code editor

    [ObservableProperty] private string _codes = "";
    [ObservableProperty] private string _globalCode = "";

    /// <summary>SetCodeEditor: one Codes entry per editor line including its EOL; ScInclude = project-wide CodeFrameData.Global.</summary>
    public void CommitCodes()
    {
        if (_loading || DataObj is not CodeEditorData d) return;
        var lines = SplitKeepingEol(Codes);
        var global = GlobalCode;
        var cf = Data.CodeFrameData;
        var same = string.Concat((IEnumerable<string?>?)d.Codes ?? []) == Codes && (cf?.Global ?? "") == global;
        if (same) return;
        Doc.Edit(Localizer.Instance.T("CodeEditor:$this.Text"), () =>
        {
            d.Codes = new ArrayList<string?>();
            foreach (var l in lines) d.Codes.Add(l);
            if (cf != null) cf.Global = global;
        });
    }

    internal static List<string> SplitKeepingEol(string text)
    {
        var result = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            result.Add(text[start..(i + 1)]);
            start = i + 1;
        }
        if (start < text.Length) result.Add(text[start..]);
        return result;
    }
}

public sealed record SignButton(string Sign, string? TipKey)
{
    public string Tip => TipKey == null ? Sign : Localizer.Instance.T(TipKey);
    public string Caption => Sign == " " ? "␣" : Sign;
}

/// <summary>One "1 Parameter" row: localized label, original SetParaDesc help, editable or fixed candidate list.</summary>
public sealed partial class ParameterRowViewModel : ObservableObject
{
    private readonly BlockPropertiesViewModel _owner;
    private bool _reverting;
    private string _committed;

    public ParameterRowViewModel(BlockPropertiesViewModel owner, int index, ParameterClass parameter, string value, IReadOnlyList<string> candidates)
    {
        _owner = owner;
        Index = index;
        Parameter = parameter;
        _value = value;
        _committed = value;
        Candidates = candidates;
    }

    public int Index { get; }
    public ParameterClass Parameter { get; }
    public IReadOnlyList<string> Candidates { get; }

    /// <summary>ComboBoxDisable (hardware/enum): pick only.</summary>
    public bool IsFixed => Parameter.IsHardwareType || Parameter.IsEnumType;

    public string Label => LabelFor(Parameter, Localizer.Instance.Language);

    public string Description => _owner.ParameterDescription(Parameter);

    [ObservableProperty] private string _value;

    /// <summary>Original label: NameCHS/NameCHT/NameDefault by culture when non-empty, else Name.</summary>
    public static string LabelFor(ParameterClass p, Core.Library.UiLanguage lang) => lang switch
    {
        Core.Library.UiLanguage.CHS when !string.IsNullOrEmpty(p.NameCHS) => p.NameCHS!,
        Core.Library.UiLanguage.CHT when !string.IsNullOrEmpty(p.NameCHT) => p.NameCHT!,
        Core.Library.UiLanguage.Default when !string.IsNullOrEmpty(p.NameDefault) => p.NameDefault!,
        _ => p.Name ?? "",
    };

    /// <summary>Original SetParaDesc.</summary>
    public static string DescriptionFor(ParameterClass p) => DescriptionFor(p, p.Description);

    internal static string DescriptionFor(ParameterClass p, string? description)
    {
        var l = Localizer.Instance;
        var colon = l.T("Xwb:Colon");
        var text = l.T("Xwb:Type") + colon + p.Type;
        if (!string.IsNullOrEmpty(p.Min)) text += "\r\n" + l.T("Xwb:Minimum") + colon + p.Min;
        if (!string.IsNullOrEmpty(p.Max)) text += "\r\n" + l.T("Xwb:Maximum") + colon + p.Max;
        if (!string.IsNullOrEmpty(p.DefaultValue)) text += "\r\n" + l.T("Xwb:Default") + colon + p.DefaultValue;
        if (!string.IsNullOrEmpty(description)) text += "\r\n" + description;
        return text;
    }

    partial void OnValueChanged(string value)
    {
        // Fixed lists commit on selection; free text commits via Commit() (focus loss / Enter).
        if (!_reverting && IsFixed) _ = CommitAsync();
    }

    private bool _committing;

    /// <summary>One check at a time: Enter commits and the error box then takes focus (LostFocus would commit again).</summary>
    public async Task CommitAsync()
    {
        if (_reverting || _committing || Value == _committed) return;
        _committing = true;
        bool ok;
        try { ok = await _owner.SetParameterAsync(this, Value ?? ""); }
        finally { _committing = false; }
        if (ok) { _committed = Value ?? ""; return; }
        _reverting = true;
        Value = _committed;
        _reverting = false;
    }
}
