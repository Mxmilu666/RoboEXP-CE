using System.Text.RegularExpressions;
using System.Xml;
using RoboExp.Core.Build;
using RoboExp.Core.Install;
using RoboExp.Core.Model;

namespace RoboExp.Core.Library;

/// <summary>
/// The system function library of one controller: <c>Include/JMlib.xml</c> (function interfaces, LibManager.CreateLibProjInfo),
/// <c>Include/Include.xml</c> (palette bands, LibManager.GetRootNodeFromXml/GetSystemFuncNameFromXml) and the generated
/// <c>&lt;Func&gt;.h</c> stub headers the original wrote into the Include directory (PublicMethod.CreateSystemRcuFile).
/// The port writes the stubs into <see cref="VendorInstall.CacheDirectory"/> instead.
/// </summary>
public sealed partial class SystemLibrary
{
    private readonly Dictionary<string, LibFunction> _byName;
    private readonly HashSet<string> _systemNames;

    private SystemLibrary(RobotType type, IReadOnlyList<LibFunction> functions, IReadOnlyList<PaletteBand> bands, string stubDir)
    {
        Type = type;
        Functions = functions;
        Bands = bands;
        StubIncludeDirectory = stubDir;
        _byName = new Dictionary<string, LibFunction>(StringComparer.Ordinal);
        foreach (var f in functions) _byName.TryAdd(f.Name, f);
        _systemNames = new HashSet<string>(bands.SelectMany(b => b.FunctionNames), StringComparer.Ordinal);
    }

    public RobotType Type { get; }
    public IReadOnlyList<LibFunction> Functions { get; }

    /// <summary>Palette bands from Include.xml.</summary>
    public IReadOnlyList<PaletteBand> Bands { get; }

    /// <summary>Directory holding the generated <c>&lt;Func&gt;.h</c> stubs and case aliases; pass it as an include dir.</summary>
    public string StubIncludeDirectory { get; }

    public LibFunction? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>
    /// LibManager.CheckIsSystemFuncName: case-sensitive membership in the <c>FuncName</c> entries of Include.xml.
    /// </summary>
    public bool IsSystemFunction(string name) => _systemNames.Contains(name);

    /// <summary>Library source every stub includes (FilePathInfo.LibSourceFilePath: JMLib32.c for QRCU, else JMLib.c).</summary>
    public static string LibSourceFileName(RobotType type) => type.SoftwareType == SoftwareType.QRCU ? "JMLib32.c" : "JMLib.c";

    /// <summary>Exact stub header text of PublicMethod.CreateSystemRcuFile.</summary>
    public static string StubHeaderText(string functionName, RobotType type)
    {
        var guard = "_" + functionName.ToUpperInvariant() + "_";
        return "#ifndef " + guard + "\r\n#define " + guard + "\r\n#include <" + LibSourceFileName(type) + ">\r\n#endif\r\n";
    }

    public static string StubDirectoryFor(RobotType type) => Path.Combine(VendorInstall.CacheDirectory, type.Name, "stubs");

    /// <summary>Parses the library and (idempotently) generates the stub headers and case aliases.</summary>
    public static SystemLibrary Load(VendorInstall install, RobotType type)
    {
        var includeDir = install.IncludeDirectory(type);
        var functions = ParseJmLib(FindRequired(includeDir, "JMlib.xml"), includeDir);
        var includeXml = VendorInstall.FindChildIgnoreCase(includeDir, "Include.xml");
        var bands = includeXml == null ? Array.Empty<PaletteBand>() : ParseInclude(includeXml);

        var stubDir = StubDirectoryFor(type);
        Directory.CreateDirectory(stubDir);
        foreach (var f in functions)
            WriteIfChanged(Path.Combine(stubDir, f.Name + ".h"), Gbk.Encoding.GetBytes(StubHeaderText(f.Name, type)));
        WriteCaseAliases(includeDir, stubDir, type, functions);
        return new SystemLibrary(type, functions, bands, stubDir);
    }

    private static string FindRequired(string dir, string name) =>
        VendorInstall.FindChildIgnoreCase(dir, name) ?? throw new FileNotFoundException($"Missing {name} in {dir}");

    internal static IReadOnlyList<LibFunction> ParseJmLib(string path, string includeDir)
    {
        var doc = new XmlDocument();
        doc.Load(path);
        var iconsDir = VendorInstall.FindChildIgnoreCase(includeDir, "Icons");
        var list = new List<LibFunction>();
        if (doc.DocumentElement is not { } root) return list;
        foreach (XmlNode func in root.ChildNodes)
        {
            if (func.LocalName != "Func") continue;
            var name = Text(func, "Name");
            if (name.Length == 0) continue;
            var parameters = new List<LibParam>();
            foreach (XmlNode p in func.ChildNodes)
            {
                if (p.LocalName != "Param") continue;
                parameters.Add(new LibParam(Text(p, "Name"), Text(p, "Type"), Text(p, "Min"), Text(p, "Max"), Text(p, "Default"),
                    LocalizedText.FromSubNodes(p, "Des")));
            }
            var icon = iconsDir == null ? null : VendorInstall.FindChildIgnoreCase(iconsDir, name + ".png");
            list.Add(new LibFunction(
                name, Text(func, "Version"), Text(func, "Author"),
                LocalizedText.FromAttributes(func.SelectSingleNode("Caption")),
                LocalizedText.FromAttributes(func.SelectSingleNode("Description")),
                Text(func, "ReturnType"),
                LocalizedText.FromAttributes(func.SelectSingleNode("ReturnDes")),
                parameters, icon));
        }
        return list;
    }

    internal static IReadOnlyList<PaletteBand> ParseInclude(string path)
    {
        var doc = new XmlDocument();
        doc.Load(path);
        var list = new List<PaletteBand>();
        if (doc.DocumentElement is not { } root) return list;
        foreach (XmlNode band in root.ChildNodes)
        {
            if (band.LocalName != "Band") continue;
            var names = new List<string>();
            foreach (XmlNode n in band.ChildNodes)
                if (n.LocalName == "FuncName" && n.InnerText.Trim() is { Length: > 0 } fn) names.Add(fn);
            list.Add(new PaletteBand(LocalizedText.FromAttributes(band), names) { IsShown = ParseBool(band.Attributes?.GetNamedItem("IsShow")?.Value) });
        }
        return list;
    }

    private static bool ParseBool(string? value)
    {
        if (value == null) return true;
        try { return XmlConvert.ToBoolean(value); } catch (FormatException) { return true; }
    }

    private static string Text(XmlNode node, string child) => node.SelectSingleNode(child)?.InnerText.Trim() ?? "";

    [GeneratedRegex("""^\s*#\s*include\s*[<"]([^>"]+)[>"]""", RegexOptions.Multiline)]
    private static partial Regex IncludeDirective();

    /// <summary>
    /// Windows resolved <c>#include</c> names case-insensitively (vendor ships <c>jmlib.c</c>, sources say <c>JMLib.c</c>).
    /// For every name referenced by the stubs or the Include directory's own sources that exists there only with different case,
    /// write a forwarding header of that exact name into the stub directory (searched after the Include directory).
    /// </summary>
    private static void WriteCaseAliases(string includeDir, string stubDir, RobotType type, IReadOnlyList<LibFunction> functions)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal) { LibSourceFileName(type) };
        foreach (var file in Directory.EnumerateFiles(includeDir))
        {
            var ext = Path.GetExtension(file);
            if (!ext.Equals(".c", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".h", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (Match m in IncludeDirective().Matches(Gbk.Encoding.GetString(File.ReadAllBytes(file))))
                referenced.Add(m.Groups[1].Value.Replace('\\', '/'));
        }
        var stubNames = new HashSet<string>(functions.Select(f => f.Name + ".h"), StringComparer.OrdinalIgnoreCase);
        foreach (var name in referenced)
        {
            if (name.Contains("..") || Path.IsPathRooted(name) || stubNames.Contains(name)) continue;
            var exact = Path.Combine(includeDir, name);
            if (File.Exists(exact) && HasExactCase(includeDir, name)) continue;
            var actual = ResolveIgnoreCase(includeDir, name);
            if (actual == null) continue;
            var alias = Path.Combine(stubDir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(alias)!);
            WriteIfChanged(alias, Gbk.Encoding.GetBytes("#include \"" + actual.Replace('\\', '/') + "\"\r\n"));
        }
    }

    private static bool HasExactCase(string dir, string relative)
    {
        foreach (var segment in relative.Split('/'))
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any(e => Path.GetFileName(e) == segment)) return false;
            dir = Path.Combine(dir, segment);
        }
        return true;
    }

    private static string? ResolveIgnoreCase(string dir, string relative)
    {
        string? current = dir;
        foreach (var segment in relative.Split('/'))
        {
            current = VendorInstall.FindChildIgnoreCase(current, segment);
            if (current == null) return null;
        }
        return File.Exists(current) ? current : null;
    }

    private static void WriteIfChanged(string path, byte[] content)
    {
        try
        {
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content)) return;
        }
        catch (IOException) { }
        var tmp = path + "." + Environment.ProcessId + "." + Environment.CurrentManagedThreadId + ".tmp";
        File.WriteAllBytes(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
