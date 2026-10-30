using System.Globalization;
using RoboExp.Core.Model;

namespace RoboExp.App.Properties;

/// <summary>Original <c>CheckValue</c> + <c>PublicInfo.CheckIsBaseType/GetBaseType</c>: literal/name validation of the property editors.</summary>
public static class ValueCheck
{
    private static readonly string[] KeyWords =
    [
        "auto", "break", "case", "char", "const", "continue", "default", "do", "double", "else",
        "enum", "extern", "float", "for", "goto", "if", "int", "long", "register", "return",
        "short", "signed", "sizeof", "static", "struct", "switch", "typedef", "union", "unsigned", "void",
        "volatile", "while",
    ];

    private static readonly string[] SystemUsed = ["inport", "outport"];

    /// <summary>Original <c>PublicInfo.m_BaseType</c>.</summary>
    public static IReadOnlyList<string> BaseTypes { get; } = ["int", "char", "long", "unsigned char", "unsigned int", "unsigned long", "double", "string"];

    public static bool IsBaseType(string? type) => type != null && BaseTypes.Contains(type);

    public static bool IsKeyWord(string s) => KeyWords.Contains(s);
    public static bool IsSystemUsed(string s) => SystemUsed.Contains(s);

    /// <summary>Types whose int/unsigned int are 32-bit in the original (<c>IsInt</c>/<c>IsUnInt</c>).</summary>
    private static bool WideInt(SoftwareType t) =>
        t is SoftwareType.X1RCU or SoftwareType.X2RCU or SoftwareType.X3RCU or SoftwareType.X4RCU or SoftwareType.E2RCU or SoftwareType.IQrobot;

    private static bool TryLong(string s, out long v) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
    private static bool IsQuotedChar(string s) => s.Length == 3 && s[0] == '\'' && s[2] == '\'';

    /// <summary>Original <c>IsValueOfType</c>.</summary>
    public static bool IsValueOfType(string? value, string? baseType, SoftwareType software)
    {
        if (string.IsNullOrEmpty(value)) return false;
        long v;
        switch (baseType)
        {
            case "char": return IsQuotedChar(value) || (TryLong(value, out v) && v is >= -128 and <= 127);
            case "unsigned char": return IsQuotedChar(value) || (TryLong(value, out v) && v is >= 0 and <= 255);
            case "string": return value.StartsWith('"') && value.EndsWith('"');
            case "int":
                if (!TryLong(value, out v)) return false;
                return WideInt(software) ? v is >= int.MinValue and <= int.MaxValue : v is >= -32768 and <= 32767;
            case "unsigned int":
                if (!TryLong(value, out v)) return false;
                return WideInt(software) ? v is >= 0 and <= uint.MaxValue : v is >= 0 and <= 65535;
            case "long": return TryLong(value, out v) && v is >= int.MinValue and <= int.MaxValue;
            // Original upper bound typo 42949667295 kept.
            case "unsigned long": return TryLong(value, out v) && v is >= 0 and <= 42949667295L;
            case "double": return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            default: return false;
        }
    }

    /// <summary>Original <c>IsMore(a, b)</c>: a &gt; b; quoted chars compare ordinally; unparsable → true.</summary>
    public static bool IsMore(string a, string b)
    {
        if (IsQuotedChar(a) && IsQuotedChar(b) && string.CompareOrdinal(a, b) > 0) return true;
        if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return x > y;
        return true;
    }

    /// <summary>Original <c>IsRightVarName</c> (ASCII letters/underscore first, then letters/digits/underscore).</summary>
    public static bool IsRightVarName(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        static bool Letter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';
        if (!Letter(s[0])) return false;
        for (var i = 1; i < s.Length; i++)
            if (!Letter(s[i]) && s[i] is < '0' or > '9') return false;
        return true;
    }

    public enum ParamError { None, DataType, MoreThanMax, LessThanMin }

    /// <summary>
    /// Validation of a base-type parameter value in PropertyFrmNew.m_propertyBag_SetValue: empty or a known
    /// variable/parameter name passes; otherwise the literal must match the type and lie in [Min, Max].
    /// </summary>
    public static ParamError CheckParameter(ParameterClass param, string value, Func<string, bool> isKnownName, SoftwareType software)
    {
        if (value.Length == 0 || isKnownName(value)) return ParamError.None;
        if (!IsValueOfType(value, param.Type, software)) return ParamError.DataType;
        if (!string.IsNullOrEmpty(param.Max) && IsMore(value, param.Max)) return ParamError.MoreThanMax;
        if (!string.IsNullOrEmpty(param.Min) && IsMore(param.Min, value)) return ParamError.LessThanMin;
        return ParamError.None;
    }

    public static string ErrorKey(ParamError e) => e switch
    {
        ParamError.DataType => "Xwb:PropertyFrm_ErrDataType",
        ParamError.MoreThanMax => "Xwb:PropertyFrm_ErrMoreMax",
        ParamError.LessThanMin => "Xwb:PropertyFrm_ErrLessMin",
        _ => "",
    };
}
