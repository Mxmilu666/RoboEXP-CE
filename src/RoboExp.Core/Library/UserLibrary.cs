using System.Text;
using System.Xml;
using RoboExp.Core.Install;
using RoboExp.Core.Model;
using RoboExp.Core.Rcu;

namespace RoboExp.Core.Library;

/// <summary>
/// A function entry of the library tree (original <c>IconNode</c> below a band). For system functions
/// <see cref="Path"/> is the function name (the original stored <c>Include\&lt;Name&gt;.rcu</c>); for user functions it is
/// the full path of the function's .rcu file (<c>&lt;FuncName&gt;</c> text of UserInclude.xml).
/// </summary>
public sealed class UserLibraryFunction
{
    public UserLibraryFunction(string path, bool isSystem)
    {
        Path = path;
        IsSystem = isSystem;
    }

    public string Path { get; internal set; }
    public bool IsSystem { get; internal set; }
    public bool IsShown { get; internal set; } = true;

    /// <summary>User functions: the signature read from <see cref="Path"/>; null when the file is missing or not a function.</summary>
    public ProjectInfo? ProjectInfo { get; internal set; }

    /// <summary>System functions: the JMlib.xml entry; null when Include.xml names a function JMlib.xml lacks.</summary>
    public LibFunction? SystemFunction { get; internal set; }

    /// <summary>Function name: the system name, or the loaded <see cref="ProjectInfo"/>'s name (null when not loaded).</summary>
    public string? Name => IsSystem ? Path : ProjectInfo?.ProjectName;

    /// <summary>Original: tree node white when its ProjectInfo loaded, light gray otherwise.</summary>
    public bool IsLoaded => IsSystem ? SystemFunction != null : ProjectInfo != null;

    internal UserLibraryFunction Clone() => new(Path, IsSystem) { IsShown = IsShown, ProjectInfo = ProjectInfo, SystemFunction = SystemFunction };
}

/// <summary>A palette band of the library tree (original top-level <c>IconNode</c>).</summary>
public sealed class UserBand
{
    private readonly List<UserLibraryFunction> _functions = [];

    public UserBand(LocalizedText caption) => Caption = caption;

    public LocalizedText Caption { get; internal set; }
    public bool IsShown { get; internal set; } = true;
    public IReadOnlyList<UserLibraryFunction> Functions => _functions;
    internal List<UserLibraryFunction> FunctionList => _functions;

    /// <summary>The band holds a system function, so it cannot be removed (LibManager.CheckCanRemoveBand).</summary>
    public bool HasSystemFunction => _functions.Any(f => f.IsSystem);

    internal UserBand CloneShallow() => new(Caption) { IsShown = IsShown };
}

/// <summary>
/// Port of the library part of <c>LibManager</c>: the palette tree = UserInclude.xml (user order, user bands, user
/// functions given by .rcu path, show/hide flags) merged with the system bands of Include.xml, edited by SystemLibDlg
/// and saved with <c>SaveAsXmlFile</c>.
/// <para>The original kept UserInclude.xml in <c>Robots/&lt;TYPE&gt;/Resources</c>. The port never writes into the vendor
/// install: it saves to <see cref="UserIncludePath"/> and falls back to the vendor file for reading.</para>
/// </summary>
public sealed class UserLibrary
{
    public const string FileName = "UserInclude.xml";

    /// <summary>Message keys (CWRM) returned by <see cref="AddFunction"/> / <see cref="LoadFuncProjectInfo(string, out int)"/> callers.</summary>
    public const string ErrorOpenFailed = "OpenFileFaild", ErrorNotSubroutine = "NotSubroutineFile", ErrorNameExists = "SubroutineNameExists";

    private readonly List<UserBand> _bands;
    private readonly SystemCatalog _system;

    /// <summary>What the merge needs from <see cref="SystemLibrary"/>: Include.xml bands and the JMlib.xml lookup.</summary>
    private sealed record SystemCatalog(RobotType Type, IReadOnlyList<PaletteBand> Bands, Func<string, LibFunction?> Find)
    {
        private readonly HashSet<string> _names = Bands.SelectMany(b => b.FunctionNames).ToHashSet(StringComparer.Ordinal);
        public bool IsSystemFunction(string name) => _names.Contains(name);
    }

    private UserLibrary(SystemCatalog system, UiLanguage language, List<UserBand> bands)
    {
        _system = system;
        Language = language;
        _bands = bands;
    }

    public UiLanguage Language { get; }
    public RobotType Type => _system.Type;
    public IReadOnlyList<UserBand> Bands => _bands;

    /// <summary>Raised after every successful mutation (original LibManager.NodeChanged).</summary>
    public event EventHandler? Changed;

    /// <summary>Writable UserInclude.xml of the port.</summary>
    public static string UserIncludePath(RobotType type) => System.IO.Path.Combine(VendorInstall.CacheDirectory, type.Name, FileName);

    /// <summary>The original's UserInclude.xml (<c>FilePathInfo.UserIncludeXmlFilePath</c>), read only as a fallback.</summary>
    public static string VendorUserIncludePath(VendorInstall install, RobotType type) =>
        System.IO.Path.Combine(install.RobotDirectory(type), "Resources", FileName);

    /// <summary>The file <see cref="Load"/> reads: the port's file, else the vendor's, else null.</summary>
    public static string? ExistingUserIncludePath(VendorInstall? install, RobotType type)
    {
        var own = UserIncludePath(type);
        if (File.Exists(own)) return own;
        if (install == null) return null;
        var resources = VendorInstall.FindChildIgnoreCase(install.RobotDirectory(type), "Resources");
        return resources == null ? null : VendorInstall.FindChildIgnoreCase(resources, FileName);
    }

    /// <summary>LibManager constructor: user tree merged with the system bands, deduplicated, user functions loaded.</summary>
    public static UserLibrary Load(VendorInstall? install, SystemLibrary system, UiLanguage language)
    {
        var path = ExistingUserIncludePath(install, system.Type);
        string? xml = null;
        if (path != null)
        {
            try { xml = File.ReadAllText(path); }
            catch (IOException) { xml = null; }
        }
        return Create(system.Type, system.Bands, system.Find, language, xml);
    }

    /// <summary>
    /// Builds the tree from explicit inputs: <paramref name="systemBands"/> (Include.xml), <paramref name="findSystem"/>
    /// (JMlib.xml lookup) and the UserInclude.xml text (null or malformed = none).
    /// </summary>
    public static UserLibrary Create(RobotType type, IReadOnlyList<PaletteBand> systemBands, Func<string, LibFunction?> findSystem,
        UiLanguage language, string? userIncludeXml)
    {
        var system = new SystemCatalog(type, systemBands, findSystem);
        List<UserBand> user = [];
        if (userIncludeXml != null)
        {
            try { user = ParseUserXml(userIncludeXml, system); }
            catch (XmlException) { user = []; }
        }
        Combine(user, SystemBands(system), language);
        RemoveSamePath(user);
        foreach (var f in user.SelectMany(b => b.FunctionList)) LoadFunction(f, system);
        RemoveSameName(user);
        return new UserLibrary(system, language, user);
    }

    /// <summary>A deep copy of the tree (SystemLibDlg edits a copy; Cancel discards it).</summary>
    public UserLibrary Clone()
    {
        var bands = _bands.Select(b =>
        {
            var c = b.CloneShallow();
            c.FunctionList.AddRange(b.FunctionList.Select(f => f.Clone()));
            return c;
        }).ToList();
        return new UserLibrary(_system, Language, bands);
    }

    public string CaptionOf(UserBand band) => band.Caption.Get(Language);

    // ---- queries (LibManager.GetBand/GetFuncNode/GetProjectInfo) ----

    public UserBand? GetBand(string caption) =>
        _bands.FirstOrDefault(b => string.Equals(CaptionOf(b), caption, StringComparison.OrdinalIgnoreCase));

    /// <summary>LibManager.GetFuncNode: a loaded user function by name (case-sensitive) or .rcu path (case-insensitive).</summary>
    public UserLibraryFunction? GetFunction(string nameOrPath) =>
        _bands.SelectMany(b => b.FunctionList).FirstOrDefault(f => f.ProjectInfo is { } info
            && (string.Equals(info.ProjectName, nameOrPath, StringComparison.Ordinal)
                || string.Equals(info.ProjectPath, nameOrPath, StringComparison.OrdinalIgnoreCase)));

    /// <summary>LibManager.GetProjectInfo for user functions (system signatures come from <see cref="SystemLibrary"/>).</summary>
    public ProjectInfo? GetProjectInfo(string nameOrPath) => GetFunction(nameOrPath)?.ProjectInfo;

    private UserLibraryFunction? GetFunctionByPath(string path) =>
        _bands.SelectMany(b => b.FunctionList).FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));

    private UserBand? BandOf(UserLibraryFunction function) => _bands.FirstOrDefault(b => b.FunctionList.Contains(function));

    private bool Contains(UserBand band) => _bands.Contains(band);

    // ---- edits (LibManager) ----

    /// <summary>LibManager.AddBand: an existing band of that caption (case-insensitive) or a new empty one at the end.</summary>
    public UserBand AddBand(string caption)
    {
        if (GetBand(caption) is { } existing) return existing;
        var band = new UserBand(new LocalizedText(caption, caption, caption));
        _bands.Add(band);
        OnChanged();
        return band;
    }

    public bool CanRemoveBand(UserBand band) => Contains(band) && !band.HasSystemFunction;

    public bool RemoveBand(UserBand band)
    {
        if (!CanRemoveBand(band)) return false;
        _bands.Remove(band);
        OnChanged();
        return true;
    }

    public bool CanRemoveFunction(UserLibraryFunction function) => BandOf(function) != null && !function.IsSystem;

    public bool RemoveFunction(UserLibraryFunction function)
    {
        if (!CanRemoveFunction(function)) return false;
        BandOf(function)!.FunctionList.Remove(function);
        OnChanged();
        return true;
    }

    /// <summary>
    /// LibManager.AddFuncNode: adds the function .rcu at <paramref name="rcuPath"/> to <paramref name="band"/>.
    /// Returns the existing entry when the path or the function name is already in the library;
    /// <paramref name="errorKey"/> is a CWRM message key (<see cref="ErrorOpenFailed"/>, <see cref="ErrorNotSubroutine"/>,
    /// <see cref="ErrorNameExists"/>) or null.
    /// </summary>
    public UserLibraryFunction? AddFunction(UserBand band, string rcuPath, out string? errorKey)
    {
        errorKey = null;
        if (!Contains(band)) return null;
        if (GetFunction(rcuPath) is { } loaded) return loaded;
        if (GetFunctionByPath(rcuPath) is { } failed)
        {
            failed.ProjectInfo = LoadFuncProjectInfo(rcuPath, out var reloadErr);
            errorKey = ErrorKey(reloadErr);
            if (failed.ProjectInfo != null) OnChanged();
            return failed;
        }
        var info = LoadFuncProjectInfo(rcuPath, out var err);
        if (info == null)
        {
            errorKey = ErrorKey(err);
            return null;
        }
        if (GetFunction(info.ProjectName ?? "") is { } sameName)
        {
            errorKey = ErrorNameExists;
            return sameName;
        }
        var node = new UserLibraryFunction(info.ProjectPath ?? rcuPath, isSystem: false) { ProjectInfo = info };
        band.FunctionList.Add(node);
        OnChanged();
        return node;
    }

    public bool SetShown(UserBand band, bool shown)
    {
        if (!Contains(band)) return false;
        band.IsShown = shown;
        OnChanged();
        return true;
    }

    public bool SetShown(UserLibraryFunction function, bool shown)
    {
        if (BandOf(function) == null) return false;
        function.IsShown = shown;
        OnChanged();
        return true;
    }

    /// <summary>LibManager.MoveUp (band): one position up.</summary>
    public bool MoveUp(UserBand band)
    {
        var i = _bands.IndexOf(band);
        if (i <= 0) return false;
        _bands.RemoveAt(i);
        _bands.Insert(i - 1, band);
        OnChanged();
        return true;
    }

    /// <summary>LibManager.MoveDown (band): one position down.</summary>
    public bool MoveDown(UserBand band)
    {
        var i = _bands.IndexOf(band);
        if (i < 0 || i >= _bands.Count - 1) return false;
        _bands.RemoveAt(i);
        _bands.Insert(i + 1, band);
        OnChanged();
        return true;
    }

    /// <summary>LibManager.MoveUp (function): up within its band; from the top of a band to the end of the previous band.</summary>
    public bool MoveUp(UserLibraryFunction function)
    {
        if (BandOf(function) is not { } band) return false;
        var i = _bands.IndexOf(band);
        var j = band.FunctionList.IndexOf(function);
        if (j > 0)
        {
            band.FunctionList.RemoveAt(j);
            band.FunctionList.Insert(j - 1, function);
        }
        else if (i > 0)
        {
            band.FunctionList.RemoveAt(j);
            _bands[i - 1].FunctionList.Add(function);
        }
        else return false;
        OnChanged();
        return true;
    }

    /// <summary>LibManager.MoveDown (function): down within its band; from the bottom of a band to the start of the next band.</summary>
    public bool MoveDown(UserLibraryFunction function)
    {
        if (BandOf(function) is not { } band) return false;
        var i = _bands.IndexOf(band);
        var j = band.FunctionList.IndexOf(function);
        if (j < band.FunctionList.Count - 1)
        {
            band.FunctionList.RemoveAt(j);
            band.FunctionList.Insert(j + 1, function);
        }
        else if (i < _bands.Count - 1)
        {
            band.FunctionList.RemoveAt(j);
            _bands[i + 1].FunctionList.Insert(0, function);
        }
        else return false;
        OnChanged();
        return true;
    }

    /// <summary>LibManager.ResetSystemDefault: the tree becomes Include.xml's bands only.</summary>
    public void ResetSystemDefault()
    {
        var bands = SystemBands(_system);
        RemoveSamePath(bands);
        foreach (var f in bands.SelectMany(b => b.FunctionList)) LoadFunction(f, _system);
        RemoveSameName(bands);
        _bands.Clear();
        _bands.AddRange(bands);
        OnChanged();
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    // ---- persistence ----

    /// <summary>Saves to <see cref="UserIncludePath"/> (LibManager.SaveAsXmlFile on SystemLibDlg OK).</summary>
    public void Save()
    {
        var path = UserIncludePath(_system.Type);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ToXml(), new UTF8Encoding(false));
    }

    /// <summary>
    /// LibManager.SaveAsXmlFile: comment, <c>&lt;root&gt;</c>, one <c>&lt;Band Default CHS CHT IsShow&gt;</c> per band with
    /// <c>&lt;FuncName IsSystem IsShow&gt;path&lt;/FuncName&gt;</c> children. A band whose caption matches an Include.xml band
    /// takes that band's three captions.
    /// </summary>
    public string ToXml()
    {
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true, NewLineChars = "\r\n" };
        using (var w = XmlWriter.Create(sb, settings))
        {
            w.WriteComment("RoboExp Include File Information");
            w.WriteStartElement("root");
            foreach (var band in _bands)
            {
                var caption = CaptionOf(band);
                var text = _system.Bands.FirstOrDefault(b => b.Caption.Get(Language) == caption)?.Caption ?? band.Caption;
                w.WriteStartElement("Band");
                w.WriteAttributeString("Default", text.Default);
                w.WriteAttributeString("CHS", text.Chs ?? text.Default);
                w.WriteAttributeString("CHT", text.Cht ?? text.Default);
                w.WriteAttributeString("IsShow", band.IsShown ? "true" : "false");
                foreach (var f in band.FunctionList)
                {
                    w.WriteStartElement("FuncName");
                    w.WriteAttributeString("IsSystem", f.IsSystem ? "true" : "false");
                    w.WriteAttributeString("IsShow", f.IsShown ? "true" : "false");
                    w.WriteString(f.Path);
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        return sb.ToString();
    }

    /// <summary>
    /// LibManager.GetRootNodeFromXml(isSystem: false). Entries flagged <c>IsSystem</c> (the original wrote
    /// <c>…\Include\&lt;Name&gt;.rcu</c>) are reduced to the function name when it is a system function.
    /// </summary>
    private static List<UserBand> ParseUserXml(string xml, SystemCatalog system)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        var bands = new List<UserBand>();
        if (doc.DocumentElement is not { } root) return bands;
        foreach (XmlNode b in root.ChildNodes)
        {
            if (b.LocalName != "Band") continue;
            var band = new UserBand(LocalizedText.FromAttributes(b)) { IsShown = ParseBool(b.Attributes?.GetNamedItem("IsShow")?.Value, true) };
            foreach (XmlNode f in b.ChildNodes)
            {
                if (f.LocalName != "FuncName") continue;
                var text = f.InnerText.Trim();
                var isSystem = ParseBool(f.Attributes?.GetNamedItem("IsSystem")?.Value, false);
                if (isSystem && SystemKey(text) is { } key && system.IsSystemFunction(key)) text = key;
                band.FunctionList.Add(new UserLibraryFunction(text, isSystem) { IsShown = ParseBool(f.Attributes?.GetNamedItem("IsShow")?.Value, true) });
            }
            bands.Add(band);
        }
        return bands;
    }

    /// <summary>File name without directory (either separator) and without a <c>.rcu</c> extension.</summary>
    private static string? SystemKey(string text)
    {
        var name = text[(text.LastIndexOfAny(['\\', '/']) + 1)..];
        if (name.EndsWith(".rcu", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Length == 0 ? null : name;
    }

    private static bool ParseBool(string? value, bool fallback)
    {
        if (value == null) return fallback;
        try { return XmlConvert.ToBoolean(value); }
        catch (FormatException) { return fallback; }
    }

    private static List<UserBand> SystemBands(SystemCatalog system) => system.Bands.Select(b =>
    {
        var band = new UserBand(b.Caption) { IsShown = b.IsShown };
        band.FunctionList.AddRange(b.FunctionNames.Select(n => new UserLibraryFunction(n, isSystem: true)));
        return band;
    }).ToList();

    /// <summary>
    /// LibManager.CombineIconNode: system functions missing from the user tree are appended to the same-caption band
    /// (or a copy of their band at the end); then IsSystem is recomputed as membership in the system tree.
    /// </summary>
    private static void Combine(List<UserBand> main, List<UserBand> system, UiLanguage language)
    {
        var mainPaths = main.SelectMany(b => b.FunctionList).Select(f => f.Path).ToList();
        foreach (var sysBand in system)
        {
            foreach (var sysFunc in sysBand.FunctionList)
            {
                if (mainPaths.Any(p => string.Equals(p, sysFunc.Path, StringComparison.OrdinalIgnoreCase))) continue;
                var caption = sysBand.Caption.Get(language);
                var target = main.FirstOrDefault(b => string.Equals(b.Caption.Get(language), caption, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                {
                    target = sysBand.CloneShallow();
                    main.Add(target);
                }
                target.FunctionList.Add(sysFunc.Clone());
            }
        }
        var systemPaths = system.SelectMany(b => b.FunctionList).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in main.SelectMany(b => b.FunctionList)) f.IsSystem = systemPaths.Contains(f.Path);
    }

    /// <summary>LibManager.RemoveSamePathNode: later entries with an already seen path (case-insensitive) are dropped.</summary>
    private static void RemoveSamePath(List<UserBand> bands)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var band in bands) band.FunctionList.RemoveAll(f => !seen.Add(f.Path));
    }

    /// <summary>LibManager.RemoveSameNameProjectInfo: later loaded entries with an already seen function name are dropped.</summary>
    private static void RemoveSameName(List<UserBand> bands)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var band in bands) band.FunctionList.RemoveAll(f => f.IsLoaded && f.Name is { } n && !seen.Add(n));
    }

    /// <summary>LibManager.LoadAllFuncProjectInfo for one entry.</summary>
    private static void LoadFunction(UserLibraryFunction f, SystemCatalog system)
    {
        if (f.IsSystem) f.SystemFunction = system.Find(f.Path);
        else if (File.Exists(f.Path)) f.ProjectInfo = LoadFuncProjectInfo(f.Path, out _);
    }

    /// <summary>
    /// LibManager.LoadFuncProjectInfo: the ProjectInfo of a FlowFunc/CodeFunc .rcu (ProjectPath = the full path).
    /// <paramref name="errInfo"/>: 0 ok, 1 unreadable, 2 not a subroutine.
    /// </summary>
    public static ProjectInfo? LoadFuncProjectInfo(string rcuPath, out int errInfo)
    {
        errInfo = 0;
        RcuFile file;
        try { file = RcuReader.Open(rcuPath); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            errInfo = 1;
            return null;
        }
        var info = file.ProjectType switch
        {
            ProjectType.FlowFunc => file.FlowData?.ProjectData?.ProjectInfo,
            ProjectType.CodeFunc => file.CodeData?.ProjectData?.ProjectInfo,
            _ => null,
        };
        if (info == null) errInfo = 2;
        return info;
    }

    /// <summary>CWRM key for a <see cref="LoadFuncProjectInfo(string, out int)"/> error code (null for 0).</summary>
    public static string? ErrorKey(int errInfo) => errInfo switch { 1 => ErrorOpenFailed, 2 => ErrorNotSubroutine, _ => null };
}
