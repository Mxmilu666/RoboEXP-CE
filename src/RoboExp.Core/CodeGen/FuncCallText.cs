using System.Text;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;

namespace RoboExp.Core.CodeGen;

/// <summary>Function-icon call text: FlowView.CreateFuncData and PropertyFrmNew.SetCodeStr.</summary>
public static class FuncCallText
{
    /// <summary>
    /// <c>[ReturnName + " = "] + ProjectName + "(" + join(", ", ValueList) + ");"</c>. The assignment prefix appears only when
    /// the callee returns something (type not null/""/"void") and ReturnName is not null/""/"void".
    /// </summary>
    public static string Build(FuncData data, ProjectInfo? callee)
    {
        var sb = new StringBuilder();
        var ret = callee?.ReturnType?.Type;
        if (ret is not (null or "" or "void") && data.ReturnName is not (null or "" or "void"))
            sb.Append(data.ReturnName).Append(" = ");
        sb.Append(data.ProjectName).Append('(');
        var first = true;
        foreach (var v in (IEnumerable<string?>?)data.ValueList ?? [])
        {
            if (!first) sb.Append(", ");
            sb.Append(v);
            first = false;
        }
        return sb.Append(");").ToString();
    }

    /// <summary>SetCodeStr: rebuilds and stores <see cref="DataObject.Code"/>; call after editing ReturnName/ValueList.</summary>
    public static string SetCodeStr(FuncData data, ProjectInfo? callee) => data.Code = Build(data, callee);

    /// <summary>
    /// FlowView.CreateFuncData: a new call of <paramref name="callee"/> with ValueList seeded from parameter defaults
    /// (or "") and Code set.
    /// </summary>
    public static FuncData CreateFuncData(ProjectInfo callee)
    {
        var data = new FuncData { ProjectName = callee.ProjectName };
        if (callee.ParamList is { Count: > 0 } ps && (data.ValueList == null || data.ValueList.Count != ps.Count))
        {
            data.ValueList = new ArrayList<string?>();
            foreach (var p in ps) data.ValueList.Add(string.IsNullOrEmpty(p.DefaultValue) ? "" : p.DefaultValue);
        }
        SetCodeStr(data, callee);
        return data;
    }
}
