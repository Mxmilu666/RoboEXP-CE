using RoboExp.App.Localization;
using RoboExp.Core.CodeGen;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;

namespace RoboExp.App.Properties;

/// <summary>
/// PropertyFrmNew's per-function panels (SetTypeVisible 4..18): the value accessors the original widgets use
/// (getValueForNameStr / setValueForNameStr / setValueForNameStrNoCheck, Tag = parameter name). Values typed into text
/// fields are validated on every change like the original but written to the model as one undoable edit when the field is
/// committed (focus loss, Enter, list selection, scroll end, closing the window).
/// </summary>
public sealed partial class BlockPropertiesViewModel
{
    private readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal);
    private string? _pendingReturn;
    private string? _pendingLoop;

    /// <summary>The per-function panel of the popup editor (never in the docked PropertyFrm, which only has the grid).</summary>
    public SpecialPanelKind SpecialPanel => Docked ? SpecialPanelKind.None : DataObj switch
    {
        FuncData f when Callee != null => SpecialPanels.ForFunction(f.ProjectName),
        BeginForData => SpecialPanelKind.For,
        _ => SpecialPanelKind.None,
    };

    public SoftwareType SoftwareType => Software;

    /// <summary>Called function name (the form's Tag).</summary>
    public string FunctionName => (DataObj as FuncData)?.ProjectName ?? "";

    /// <summary>Window caption: ProjectCaption of the called function (SetTypeVisible), else "Property".</summary>
    public string SpecialTitle => Callee is { } c && !string.IsNullOrEmpty(FunctionName)
        ? (string.IsNullOrEmpty(c.ProjectCaption) ? c.ProjectName ?? "" : c.ProjectCaption!)
        : Localizer.Instance.T("PropertyFrm:$this.Text");

    /// <summary>getValueForNameStr: index of the parameter matched by NameCHS/NameCHT/NameDefault/Name.</summary>
    internal int ParamIndex(string name)
    {
        var ps = Callee?.ParamList;
        if (ps == null) return -1;
        for (var i = 0; i < ps.Count; i++)
        {
            var p = ps[i];
            if (p.NameCHS == name || p.NameCHT == name || p.NameDefault == name || p.Name == name) return i;
        }
        return -1;
    }

    public bool HasParam(string name) => ParamIndex(name) >= 0;

    public ParameterClass? Param(string name) => ParamIndex(name) is >= 0 and var i ? Callee!.ParamList![i] : null;

    /// <summary>getValueForNameStr (pending edits included).</summary>
    public string GetValue(string name)
    {
        if (_pending.TryGetValue(name, out var p)) return p;
        var i = ParamIndex(name);
        if (i < 0 || DataObj is not FuncData f || f.ValueList == null || i >= f.ValueList.Count) return "";
        return f.ValueList[i] ?? "";
    }

    public string ReturnName => _pendingReturn ?? (DataObj as FuncData)?.ReturnName ?? "";

    public string LoopCountText => _pendingLoop ?? (DataObj as BeginForData)?.LoopCountStr ?? "";

    public string ReturnDescription => Callee?.ReturnType?.Description ?? "";

    public string FunctionDescription => Doc.Session.Library.Find(FunctionName)?.Description.Get(Localizer.Instance.Language) is { Length: > 0 } description
        ? description : Callee?.ProjectDescription ?? "";

    /// <summary>Variable list of the editable/return combos (dropdown(): "" + EnableList2, which ends with DefineVar).</summary>
    public IReadOnlyList<string> VariableItems => ["", .. BuildVariableList(includeDefine: true)];

    /// <summary>
    /// setValueForNameStr's validation for a base-type parameter: null when <paramref name="value"/> is acceptable
    /// (empty, a known variable/parameter, or a literal of the type within Min/Max), else the original message.
    /// Non-base types (ports) always pass.
    /// </summary>
    public string? Validate(string name, string value)
    {
        var p = Param(name);
        if (p == null || !ValueCheck.IsBaseType(p.Type) || value.Length == 0) return null;
        var err = ValueCheck.CheckParameter(p, value, IsKnownName, Software);
        return err == ValueCheck.ParamError.None ? null : Localizer.Instance.T(ValueCheck.ErrorKey(err));
    }

    /// <summary>Stages a value for <paramref name="name"/> (no-op when the function has no such parameter).</summary>
    public void Stage(string name, string value)
    {
        if (ParamIndex(name) < 0) return;
        _pending[name] = value;
    }

    public void StageReturn(string value) => _pendingReturn = value;

    public void StageLoop(string value) => _pendingLoop = value;

    public bool HasPending => _pending.Count > 0 || _pendingReturn != null || _pendingLoop != null;

    /// <summary>Writes the staged values (ValueList, ReturnName or LoopCountStr) as one edit and rebuilds the call text (SetCodeStr).</summary>
    public void CommitSpecial()
    {
        if (!HasPending) return;
        var values = _pending.Select(kv => (Index: ParamIndex(kv.Key), kv.Value)).Where(x => x.Index >= 0).ToList();
        var ret = _pendingReturn;
        var loop = _pendingLoop;
        _pending.Clear();
        _pendingReturn = _pendingLoop = null;
        var callee = Callee;
        switch (DataObj)
        {
            case FuncData f:
                var changed = (ret != null && ret != f.ReturnName)
                              || values.Any(v => f.ValueList == null || v.Index >= f.ValueList.Count || f.ValueList[v.Index] != v.Value);
                if (!changed) return;
                Doc.Edit(SpecialTitle, () =>
                {
                    f.ValueList ??= new ArrayList<string?>();
                    foreach (var (index, value) in values)
                    {
                        while (f.ValueList.Count <= index) f.ValueList.Add("");
                        f.ValueList[index] = value;
                    }
                    if (ret != null) f.ReturnName = ret;
                    FuncCallText.SetCodeStr(f, callee);
                });
                break;
            case BeginForData d when loop != null && loop != d.LoopCountStr:
                Doc.Edit(Localizer.Instance.T("Xwb:PropertyFrm_LoopTimes"), () => d.LoopCountStr = loop);
                break;
        }
    }

    /// <summary>Stages and commits at once (lists, radios, port pickers).</summary>
    public void SetNow(string name, string value)
    {
        Stage(name, value);
        CommitSpecial();
    }

    /// <summary>SetNewVar from a combo's "DefineVar" item; the new variable name or null (cancel / "More…" opens the variable pane).</summary>
    public Task<string?> DefineVariableAsync() => _host.DefineNewVariableAsync(Doc);

    public Task ShowWarningAsync(string text) => _host.ShowWarningAsync(text);

    public void ShowVariables() => _host.ShowVariables();
}
