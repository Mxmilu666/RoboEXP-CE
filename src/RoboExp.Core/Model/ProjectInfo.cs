using System.Text;

namespace RoboExp.Core.Model;

/// <summary>
/// <c>RoboExp.ProjectInfo</c>: a project's (or used function's) signature and metadata.
/// Also used as the entries of <see cref="ProjectData.UsedProjectInfoList"/>.
/// </summary>
public sealed class ProjectInfo : RcuObject
{
    public int Version { get; set; } = 100;
    public string? UserVersion { get; set; } = "1.0";
    public string? UserName { get; set; } = Environment.UserName;
    public string? ProjectName { get; set; } = "";
    public string? ProjectCaption { get; set; } = "";
    public string? ProjectDescription { get; set; } = "";

    /// <summary>Absolute path of the .rcu as saved by the original (a Windows path in vendor files).</summary>
    public string? ProjectPath { get; set; } = "";

    public ReturnType? ReturnType { get; set; } = new();

    /// <summary>Serialized as <c>System.Drawing.Image</c> (a Bitmap).</summary>
    public EncodedImage? ProjectIcon { get; set; }

    public SoftwareType SoftwareType { get; set; }
    public OuterType OuterType { get; set; }
    public ProjectType ProjectType { get; set; }
    public ArrayList<ParameterClass>? ParamList { get; set; } = new();
    public ArrayList<UsedHardObject>? SubFuncUsedHardObjectList { get; set; } = new();
    public ArrayList<HardwareInstance>? CurUsedHardInstanceList { get; set; } = new();
    public EventArrayList<UsedGlobalVarObject>? SubFuncUsedGlobalVarObjectList { get; set; } = new();
    public EventArrayList<VarStruct>? CurUsedGlobalVarList { get; set; } = new();

    /// <summary>Original <c>GetFuncDeclareCodeLine</c>: <c>"ret Name(type a, type b)"</c> (no semicolon).</summary>
    public string GetFuncDeclareCodeLine()
    {
        var ret = ReturnType?.Type is { } t && t != "void" ? t : "void";
        var sb = new StringBuilder().Append(ret).Append(' ').Append(ProjectName).Append('(');
        var first = true;
        foreach (var p in ParamList ?? [])
        {
            if (!first) sb.Append(", ");
            sb.Append(p.Type).Append(' ').Append(p.Name);
            first = false;
        }
        return sb.Append(')').ToString();
    }

    /// <summary>Original <c>CheckParamListEquals</c>: same count, and per position same IsHardwareType and Type.</summary>
    public bool CheckParamListEquals(ProjectInfo? other)
    {
        if (other == null) return false;
        var a = ParamList ?? [];
        var b = other.ParamList ?? [];
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].IsHardwareType != b[i].IsHardwareType || !string.Equals(a[i].Type, b[i].Type, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    /// <summary>Original <c>CheckIsEquals</c>: same name, user version, parameter list and return type.</summary>
    public bool CheckIsEquals(ProjectInfo? other) =>
        other != null
        && string.Equals(ProjectName, other.ProjectName, StringComparison.Ordinal)
        && string.Equals(UserVersion, other.UserVersion, StringComparison.Ordinal)
        && CheckParamListEquals(other)
        && string.Equals(ReturnType?.Type, other.ReturnType?.Type, StringComparison.Ordinal);
}

/// <summary><c>RoboExp.ReturnType</c>.</summary>
public sealed class ReturnType : RcuObject
{
    public int Version { get; set; } = 100;
    public string? Type { get; set; } = "";
    public string? Description { get; set; } = "";
}

/// <summary>
/// <c>RoboExp.ParameterClass</c>: a function parameter. Version 100 files lack
/// <c>m_NameCHS/m_NameCHT/m_NameDefault</c> (the original only reads them when version &gt; 100).
/// </summary>
public sealed class ParameterClass : RcuObject
{
    public int Version { get; set; } = 101;
    public string? Name { get; set; } = "";
    public string? NameCHS { get; set; } = "";
    public string? NameCHT { get; set; } = "";
    public string? NameDefault { get; set; } = "";
    public string? Type { get; set; } = "";
    public string? DefaultValue { get; set; } = "";
    public string? Min { get; set; } = "";
    public string? Max { get; set; } = "";
    public string? Description { get; set; } = "";

    /// <summary>Hardware-instance parameter (<c>_X_</c> types).</summary>
    public bool IsHardwareType { get; set; }

    public bool IsEnumType { get; set; }
}

/// <summary><c>RoboExp.VarStruct</c>: a variable declaration (global var list, parameters).</summary>
public sealed class VarStruct : RcuObject
{
    public int Version { get; set; } = 100;
    public string? Type { get; set; } = "";
    public string? Name { get; set; } = "";
    public string? Description { get; set; } = "";
    public string? DefaultValue { get; set; } = "";
    public int ReturnCount { get; set; }
    public int ParamCount { get; set; }
    public bool IsParam { get; set; }
}

/// <summary><c>RoboExp.UsedHardObject</c>: hardware instances used by a called sub-function.</summary>
public sealed class UsedHardObject : RcuObject
{
    public int Version { get; set; } = 100;
    public string? ProjectName { get; set; } = "";
    public ArrayList<HardwareInstance>? HardwareInstanceList { get; set; } = new();
}

/// <summary><c>RoboExp.UsedGlobalVarObject</c>: global variables used by a called sub-function.</summary>
public sealed class UsedGlobalVarObject : RcuObject
{
    public int Version { get; set; } = 100;
    public string? ProjectName { get; set; } = "";
    public ArrayList<VarStruct>? GlobalVarList { get; set; } = new();
}
