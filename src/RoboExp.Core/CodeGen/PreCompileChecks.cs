using RoboExp.Core.Flow;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;

namespace RoboExp.Core.CodeGen;

public enum PreCompileIssueKind
{
    /// <summary>CheckParamHasSet: a reached Function icon has a blank argument (error, aborts compile).</summary>
    TooFewArgumentsToSubroutine,

    /// <summary>CheckReturnHasSet: a reached call of a non-void function has no return variable (warning).</summary>
    ReturnVarNotSet,

    /// <summary>CheckHardInstHasDefine (X3RCU/X4RCU only): a used hardware instance is not in the hardware table (error).</summary>
    HardwareInstanceNotDefined,
}

/// <summary>
/// A pre-compile finding. <see cref="Text"/> is the original output line (English CWRM strings), e.g.
/// <c>&lt;dir&gt;\&lt;Name&gt;.c:12: error:Too few arguments to Subroutine "Foo"</c>.
/// </summary>
public sealed record PreCompileIssue(PreCompileIssueKind Kind, bool IsError, GraphicObject? Object, int Line, string Name, string Text);

public sealed record PreCompileReport(IReadOnlyList<PreCompileIssue> Issues)
{
    /// <summary>True when compilation must stop (MainForm.Compile aborts on the first error).</summary>
    public bool HasError => Issues.Any(i => i.IsError);
}

/// <summary>MainForm.Compile pre-checks for flow projects plus the hardware-usage bookkeeping they rely on (BaseView/FlowView).</summary>
public static class PreCompileChecks
{
    public const string TooFewArgumentsText = "Too few arguments to Subroutine";
    public const string ReturnVarNotSetText = "Return variable has not been set";

    /// <summary>
    /// Runs the checks in the original order: param check (error stops), return check (warning), and for X3RCU/X4RCU the
    /// hardware-instance check (error). <paramref name="usedIcons"/> = Function icons reached by generation, in order
    /// (<see cref="CodeGenResult.UsedFunctionIcons"/>); BeginLines come from the same generation.
    /// </summary>
    public static PreCompileReport Run(ProjectData data, FlowGraph graph, IReadOnlyList<GraphicObject> usedIcons, SoftwareType type, string cFileDisplayPath)
    {
        var issues = new List<PreCompileIssue>();
        if (CheckParamHasSet(usedIcons) is { } p)
        {
            issues.Add(Located(PreCompileIssueKind.TooFewArgumentsToSubroutine, true, p, graph, cFileDisplayPath, TooFewArgumentsText));
            return new PreCompileReport(issues);
        }
        if (CheckReturnHasSet(data, usedIcons) is { } r)
            issues.Add(Located(PreCompileIssueKind.ReturnVarNotSet, false, r, graph, cFileDisplayPath, ReturnVarNotSetText));
        if (NeedsHardwareCheck(type))
        {
            UpdateUsedHardwareInstance(data, usedIcons);
            if (CheckHardInstHasDefine(data) is { } inst)
                issues.Add(new PreCompileIssue(PreCompileIssueKind.HardwareInstanceNotDefined, true, null, 0, inst.Name ?? "",
                    "Port " + inst.Name + " undeclared."));
        }
        return new PreCompileReport(issues);
    }

    /// <summary>Among targets only X3RCU and X4RCU run the hardware check and the compile-time HardwareInfo.c.</summary>
    public static bool NeedsHardwareCheck(SoftwareType t) => t is not (SoftwareType.Tank or SoftwareType.IQBug or SoftwareType.E3RCU
        or SoftwareType.E6RCU or SoftwareType.E7RCU or SoftwareType.M7RCU or SoftwareType.M6RCU or SoftwareType.C6RCU or SoftwareType.C1RCU
        or SoftwareType.EV5 or SoftwareType.MRCU or SoftwareType.M2RCU or SoftwareType.E2RCU or SoftwareType.IQrobot or SoftwareType.IQ2Robot);

    private static PreCompileIssue Located(PreCompileIssueKind kind, bool error, GraphicObject go, FlowGraph graph, string cFile, string message)
    {
        var line = graph.State(go).BeginLine;
        var name = go.DataObject?.ProjectName ?? "";
        return new PreCompileIssue(kind, error, go, line, name, cFile + ":" + line + (error ? ": error:" : ": warning:") + message + " \"" + name + "\"");
    }

    /// <summary>FlowView.CheckParamHasSet: first used Function icon with a null/blank argument.</summary>
    public static GraphicObject? CheckParamHasSet(IEnumerable<GraphicObject> usedIcons) =>
        usedIcons.FirstOrDefault(go => go.DataObject is FuncData f && ((IEnumerable<string?>?)f.ValueList ?? []).Any(v => v == null || v.Trim().Length == 0));

    /// <summary>FlowView.CheckReturnHasSet: first used call of a non-void callee whose ReturnName is null/""/"void".</summary>
    public static GraphicObject? CheckReturnHasSet(ProjectData data, IEnumerable<GraphicObject> usedIcons) =>
        usedIcons.FirstOrDefault(go => go.DataObject is FuncData f
            && data.GetUsedProjectInfo(f.ProjectName ?? "") is { ReturnType.Type: not (null or "void" or "") }
            && (string.IsNullOrEmpty(f.ReturnName) || f.ReturnName == "void"));

    /// <summary>BaseView.UpdateUsedHardwareInstance = UpdateSubFuncUsedHardObjectList + FlowView.UpdateCurUsedHardInstanceList.</summary>
    public static void UpdateUsedHardwareInstance(ProjectData data, IReadOnlyList<GraphicObject> usedIcons)
    {
        UpdateSubFuncUsedHardObjectList(data);
        UpdateCurUsedHardInstanceList(data, usedIcons);
    }

    public static void UpdateSubFuncUsedHardObjectList(ProjectData data)
    {
        var info = data.ProjectInfo!;
        var list = info.SubFuncUsedHardObjectList ??= new();
        list.Clear();
        foreach (var used in (IEnumerable<ProjectInfo>?)data.UsedProjectInfoList ?? [])
        {
            var sub = (IReadOnlyList<UsedHardObject>?)used.SubFuncUsedHardObjectList ?? [];
            var cur = (IReadOnlyList<HardwareInstance>?)used.CurUsedHardInstanceList ?? [];
            if (sub.Count <= 0 && cur.Count <= 0) continue;
            var obj = new UsedHardObject { ProjectName = used.ProjectName };
            foreach (var s in sub)
                foreach (var h in (IEnumerable<HardwareInstance>?)s.HardwareInstanceList ?? []) obj.HardwareInstanceList!.Add(h);
            foreach (var h in cur) obj.HardwareInstanceList!.Add(h);
            RemoveSameName(obj.HardwareInstanceList!);
            list.Add(obj);
        }
    }

    /// <summary>FlowView.UpdateCurUsedHardInstanceList: hardware-typed arguments of the reached calls, de-duplicated by name.</summary>
    public static void UpdateCurUsedHardInstanceList(ProjectData data, IReadOnlyList<GraphicObject> usedIcons)
    {
        var list = data.ProjectInfo!.CurUsedHardInstanceList ??= new();
        list.Clear();
        foreach (var go in usedIcons)
        {
            if (go.DataObject is not FuncData f) continue;
            var callee = data.GetUsedProjectInfo(f.ProjectName ?? "");
            var ps = callee?.ParamList;
            var vs = f.ValueList;
            if (ps == null || vs == null || ps.Count != vs.Count) continue;
            for (var i = 0; i < ps.Count; i++)
            {
                if (ps[i].IsHardwareType && vs[i] is { Length: not 0 } text)
                    list.Add(new HardwareInstance { Name = text, TypeName = ps[i].Type });
            }
        }
        for (var j = 0; j < list.Count; j++)
            for (var k = j + 1; k < list.Count; k++)
                if (string.Equals(list[j].Name, list[k].Name, StringComparison.Ordinal)) list.RemoveAt(k--);
    }

    private static void RemoveSameName(IList<HardwareInstance> list)
    {
        for (var i = 0; i < list.Count; i++)
            for (var n = list.Count - 1; n > i; n--)
                if (string.Equals(list[i].Name, list[n].Name, StringComparison.Ordinal)) list.RemoveAt(n);
    }

    /// <summary>BaseView.CheckHardInstHasDefine (after UpdateUsedHardwareInstance): first used instance not in the table.</summary>
    public static HardwareInstance? CheckHardInstHasDefine(ProjectData data)
    {
        var items = (IEnumerable<HardwareItem>?)data.HardwareTable?.HardwareItemList ?? [];
        bool Defined(HardwareInstance inst) => items.Any(it =>
            ((IEnumerable<HardwareInstance>?)it.InstanceList ?? []).Any(x => string.Equals(x.Name, inst.Name, StringComparison.Ordinal)));
        foreach (var inst in (IEnumerable<HardwareInstance>?)data.ProjectInfo?.CurUsedHardInstanceList ?? [])
            if (!Defined(inst)) return inst;
        foreach (var sub in (IEnumerable<UsedHardObject>?)data.ProjectInfo?.SubFuncUsedHardObjectList ?? [])
            foreach (var inst in (IEnumerable<HardwareInstance>?)sub.HardwareInstanceList ?? [])
                if (!Defined(inst)) return inst;
        return null;
    }
}
