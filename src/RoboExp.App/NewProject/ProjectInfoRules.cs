using RoboExp.App.Properties;
using RoboExp.Core.Model;

namespace RoboExp.App.NewProject;

/// <summary>Pure logic of the original ProjectInfoFrm (IsTrueParaName, IsTrueDefine, AddParameter/ModifyParameter, BtnOk).</summary>
public static class ProjectInfoRules
{
    /// <summary>IsTrueParaName: returns the XWBRM message key of the first failure, or null.</summary>
    public static string? CheckParamName(IReadOnlyList<ParameterClass> existing, int index, string name)
    {
        if (name.Length == 0) return "Xwb:ProjectInfoFrm_ParaNameIsNull";
        for (var i = 0; i < existing.Count; i++)
            if (i != index && existing[i].Name == name) return "Xwb:ProjectInfoFrm_SameParaExist";
        if (!ValueCheck.IsRightVarName(name)) return "Xwb:ProjectInfoFrm_ErrParaName";
        if (ValueCheck.IsKeyWord(name)) return "Xwb:ProjectInfoFrm_isKeyWord";
        if (ValueCheck.IsSystemUsed(name)) return "Xwb:ProjectInfoFrm_isSystemUsed";
        return null;
    }

    /// <summary>IsTrueDefine: for base types, default/min/max must be literals of the type and ordered min ≤ default ≤ max.</summary>
    public static string? CheckParamDefine(string type, string def, string min, string max, SoftwareType software)
    {
        if (!ValueCheck.IsBaseType(type)) return null;
        if (def != "" && !ValueCheck.IsValueOfType(def, type, software)) return "Xwb:ProjectInfoFrm_ErrDefault";
        if (min != "" && !ValueCheck.IsValueOfType(min, type, software)) return "Xwb:ProjectInfoFrm_ErrMin";
        if (max != "" && !ValueCheck.IsValueOfType(max, type, software)) return "Xwb:ProjectInfoFrm_ErrMax";
        if (max != "" && min != "" && ValueCheck.IsMore(min, max)) return "Xwb:ProjectInfoFrm_MinBiggerMax";
        if (def != "" && min != "" && ValueCheck.IsMore(min, def)) return "Xwb:ProjectInfoFrm_ErrLessMin";
        if (max != "" && def != "" && ValueCheck.IsMore(def, max)) return "Xwb:ProjectInfoFrm_ErrBiggerMax";
        return null;
    }

    /// <summary>
    /// AddParameter/ModifyParameter field mapping: base types keep default/min/max; other types clear them and are a
    /// hardware parameter when a hardware item has that type name, else an enum parameter.
    /// </summary>
    public static void Apply(ParameterClass p, string name, string type, string description, string def, string min, string max,
        IEnumerable<HardwareItem> hardwareItems)
    {
        p.Name = name;
        p.Type = type;
        p.Description = description;
        if (ValueCheck.IsBaseType(type))
        {
            p.DefaultValue = def;
            p.Max = max;
            p.Min = min;
            p.IsEnumType = false;
            p.IsHardwareType = false;
            return;
        }
        p.DefaultValue = p.Max = p.Min = "";
        p.IsHardwareType = hardwareItems.Any(h => h.HardwareType?.Name == type);
        p.IsEnumType = !p.IsHardwareType;
    }

    /// <summary>Parameter type choices: base types then the hardware type names (CobParType).</summary>
    public static IReadOnlyList<string> ParameterTypes(IEnumerable<HardwareItem> hardwareItems) =>
        [.. ValueCheck.BaseTypes, .. hardwareItems.Select(h => h.HardwareType?.Name ?? "").Where(n => n.Length > 0)];

    /// <summary>Return type choices: "void" + base types (CobRetType; disabled for applications).</summary>
    public static IReadOnlyList<string> ReturnTypes { get; } = ["void", .. ValueCheck.BaseTypes];

    /// <summary>BtnOk caption rule: blank → project name.</summary>
    public static string Caption(string text, string? projectName) => text.Length == 0 ? projectName ?? "" : text;
}
