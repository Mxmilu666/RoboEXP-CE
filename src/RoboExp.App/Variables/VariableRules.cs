using RoboExp.App.Properties;
using RoboExp.Core.Model;

namespace RoboExp.App.Variables;

/// <summary>Pure rules of the original VariableFrm (AddLocalVar/AddGlobalVar, IsTrueLocalVarName/IsTrueGlobalVarName, IsTrueDefault).</summary>
public static class VariableRules
{
    /// <summary>Original default type: "long" for every controller the port supports (VariableFrm line 603 list), else "unsigned char".</summary>
    public static string DefaultType(SoftwareType t) => t switch
    {
        SoftwareType.ERCU or SoftwareType.X1RCU or SoftwareType.X2RCU or SoftwareType.X3RCU or SoftwareType.X4RCU or SoftwareType.A3RCU
            or SoftwareType.E2RCU or SoftwareType.E3RCU or SoftwareType.E6RCU or SoftwareType.E7RCU or SoftwareType.M7RCU or SoftwareType.M6RCU
            or SoftwareType.C6RCU or SoftwareType.C1RCU or SoftwareType.EV5 or SoftwareType.IQ2Robot or SoftwareType.IQrobot
            or SoftwareType.MRCU or SoftwareType.M2RCU => "long",
        _ => "unsigned char",
    };

    /// <summary>Selectable types (original SelectBaseType → PublicInfo.GetBaseType).</summary>
    public static IReadOnlyList<string> Types(SoftwareType t) => t == SoftwareType.ERCU ? ["unsigned int"] : ValueCheck.BaseTypes;

    /// <summary>
    /// Next free name "<paramref name="prefix"/>&lt;n&gt;" (var / g_), unique across both lists; <paramref name="counter"/> is the
    /// form's running index (reset when the form is bound to a document) and advances like the original.
    /// </summary>
    public static string NextName(string prefix, ref int counter, IEnumerable<VarStruct> local, IEnumerable<VarStruct> global)
    {
        var used = local.Concat(global).Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        string name;
        do name = prefix + counter++;
        while (used.Contains(name));
        return name;
    }

    public static VarStruct NewVariable(string name, SoftwareType t) =>
        new() { Name = name, Type = DefaultType(t), DefaultValue = "0", Description = "" };

    public enum NameError { None, Empty, Exists, WrongName, KeyWord, SystemUsed, SystemFunction }

    public static string MessageKey(NameError e) => e switch
    {
        NameError.Empty => "Xwb:VariableFrm_ErrNameIsNull",
        NameError.Exists => "Xwb:VariableFrm_ErrVarExist",
        NameError.WrongName => "Xwb:VariableFrm_ErrWrongName",
        NameError.KeyWord => "Xwb:VariableFrm_ErrIsKeyWord",
        NameError.SystemUsed => "Xwb:VariableFrm_ErrIsUsed",
        NameError.SystemFunction => "Xwb:IsSystemSubName",
        _ => "",
    };

    /// <summary>
    /// Validates renaming entry <paramref name="index"/> of <paramref name="own"/> to <paramref name="name"/>.
    /// <paramref name="isSystemFunction"/> is only applied to local variables (the global check omits it).
    /// </summary>
    public static NameError CheckName(IReadOnlyList<VarStruct> own, int index, IEnumerable<VarStruct> other, string name, Func<string, bool>? isSystemFunction)
    {
        if (name.Length == 0) return NameError.Empty;
        for (var i = 0; i < own.Count; i++)
            if (i != index && own[i].Name == name) return NameError.Exists;
        if (other.Any(v => v.Name == name)) return NameError.Exists;
        if (!ValueCheck.IsRightVarName(name)) return NameError.WrongName;
        if (ValueCheck.IsKeyWord(name)) return NameError.KeyWord;
        if (ValueCheck.IsSystemUsed(name)) return NameError.SystemUsed;
        if (isSystemFunction != null && isSystemFunction(name)) return NameError.SystemFunction;
        return NameError.None;
    }

    /// <summary>Original IsTrueDefault: empty is allowed, otherwise must be a literal of the type.</summary>
    public static bool IsValidDefault(string type, string value, SoftwareType t) => value.Length == 0 || ValueCheck.IsValueOfType(value, type, t);
}
