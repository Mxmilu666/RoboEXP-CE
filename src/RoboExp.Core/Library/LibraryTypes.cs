using System.Xml;

namespace RoboExp.Core.Library;

/// <summary>UI culture of the original (<c>SoftwareInfo.SoftwareCulture</c>); <see cref="Default"/> is English.</summary>
public enum UiLanguage { Default, CHS, CHT }

/// <summary>
/// A text with the vendor's three cultures (<c>Default</c>/<c>CHS</c>/<c>CHT</c> attributes or sub-nodes).
/// <see cref="Get"/> falls back to <see cref="Default"/> when the requested culture is missing or empty,
/// like <c>LibManager.GetAttrText</c>/<c>GetSubNodeText</c>.
/// </summary>
public sealed record LocalizedText(string Default, string? Chs, string? Cht)
{
    public static LocalizedText Empty { get; } = new("", null, null);

    public string Get(UiLanguage lang) => lang switch
    {
        UiLanguage.CHS when !string.IsNullOrEmpty(Chs) => Chs,
        UiLanguage.CHT when !string.IsNullOrEmpty(Cht) => Cht,
        _ => Default,
    };

    /// <summary>Reads <c>{baseName}Default</c>/<c>CHS</c>/<c>CHT</c> attributes (LibManager.GetAttrText).</summary>
    internal static LocalizedText FromAttributes(XmlNode? node, string baseName = "")
    {
        if (node?.Attributes is not { } attrs) return Empty;
        return new LocalizedText(
            attrs.GetNamedItem(baseName + "Default")?.InnerText ?? "",
            attrs.GetNamedItem(baseName + "CHS")?.InnerText,
            attrs.GetNamedItem(baseName + "CHT")?.InnerText);
    }

    /// <summary>Reads <c>{baseName}Default</c>/<c>CHS</c>/<c>CHT</c> child elements (LibManager.GetSubNodeText).</summary>
    internal static LocalizedText FromSubNodes(XmlNode node, string baseName) => new(
        node.SelectSingleNode(baseName + "Default")?.InnerText ?? "",
        node.SelectSingleNode(baseName + "CHS")?.InnerText,
        node.SelectSingleNode(baseName + "CHT")?.InnerText);
}

/// <summary>One <c>&lt;Param&gt;</c> of a system library function in JMlib.xml.</summary>
public sealed record LibParam(string Name, string Type, string Min, string Max, string Default, LocalizedText Description)
{
    /// <summary>Hardware port types are written <c>_X_</c> (LibManager.CheckIsHardType).</summary>
    public bool IsHardwareType => Type.StartsWith('_') && Type.EndsWith('_');
}

/// <summary>One <c>&lt;Func&gt;</c> of JMlib.xml (the original turns each into a CodeFunc ProjectInfo).</summary>
public sealed record LibFunction(
    string Name,
    string Version,
    string Author,
    LocalizedText Caption,
    LocalizedText Description,
    string ReturnType,
    LocalizedText ReturnDescription,
    IReadOnlyList<LibParam> Params,
    string? IconPath);

/// <summary>A palette band of Include.xml: caption plus the function names listed under it, in order.</summary>
public sealed record PaletteBand(LocalizedText Caption, IReadOnlyList<string> FunctionNames)
{
    /// <summary><c>IsShow</c> attribute of the band (default true).</summary>
    public bool IsShown { get; init; } = true;
}
