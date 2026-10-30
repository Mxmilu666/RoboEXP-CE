namespace RoboExp.Core.Install;

/// <summary>
/// Read-only view of an extracted original RoboEXP installation (the directory that contains
/// <c>Robots/</c>, <c>Compiler/JMCompiler.xml</c> and <c>Resources/</c>). The port never writes into it.
/// </summary>
public sealed class VendorInstall
{
    public const string EnvVar = "ROBOEXP_HOME";

    public string Root { get; }

    public VendorInstall(string root)
    {
        if (!File.Exists(Path.Combine(root, "Compiler", "JMCompiler.xml")))
            throw new DirectoryNotFoundException($"Not a RoboEXP install (missing Compiler/JMCompiler.xml): {root}");
        Root = Path.GetFullPath(root);
    }

    public string CompilerXmlPath => Path.Combine(Root, "Compiler", "JMCompiler.xml");
    public string RobotsDirectory => Path.Combine(Root, "Robots");
    public string ResourcesDirectory => Path.Combine(Root, "Resources");

    public string RobotDirectory(RobotType type) => Path.Combine(RobotsDirectory, type.Name);

    /// <summary>The robot's include directory. The vendor uses both "Include" and "include".</summary>
    public string IncludeDirectory(RobotType type) => FindChildIgnoreCase(RobotDirectory(type), "Include")
        ?? throw new DirectoryNotFoundException($"No Include directory for {type.Name}");

    public string ExamplesDirectory(RobotType type) => Path.Combine(RobotDirectory(type), "Examples");

    /// <summary>
    /// Writable per-user directory for derived artifacts (generated library stub headers, linker overlays).
    /// </summary>
    public static string CacheDirectory
    {
        get
        {
            var dir = DataDirectory.Root;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// Resolves the install: <c>$ROBOEXP_HOME</c>, else the nearest <c>vendor/app</c> or <c>Resources/vendor/app</c> walking up from the app base directory,
    /// else the most recently imported install under <see cref="DataDirectory.VendorRoot"/> (see <see cref="Inno.VendorImporter"/>).
    /// </summary>
    public static VendorInstall Locate()
    {
        var env = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrEmpty(env)) return new VendorInstall(env);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "vendor", "app");
            if (IsInstall(candidate)) return new VendorInstall(candidate);
            var resources = Path.Combine(dir.FullName, "Resources", "vendor", "app");
            if (IsInstall(resources)) return new VendorInstall(resources);
        }
        if (Directory.Exists(DataDirectory.VendorRoot))
        {
            var imported = Directory.EnumerateDirectories(DataDirectory.VendorRoot)
                .Where(IsInstall)
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (imported != null) return new VendorInstall(imported);
        }
        throw new DirectoryNotFoundException($"RoboEXP install not found; set {EnvVar} to the extracted install directory.");
    }

    /// <summary>True when <paramref name="root"/> looks like a usable install: <c>Compiler/JMCompiler.xml</c> and <c>Robots/</c>.</summary>
    public static bool IsInstall(string root) =>
        File.Exists(Path.Combine(root, "Compiler", "JMCompiler.xml")) && Directory.Exists(Path.Combine(root, "Robots"));

    /// <summary>Case-insensitive lookup of an existing child file/dir; vendor paths mix case and Linux is case-sensitive.</summary>
    public static string? FindChildIgnoreCase(string directory, string name)
    {
        if (!Directory.Exists(directory)) return null;
        var exact = Path.Combine(directory, name);
        if (Path.Exists(exact)) return exact;
        return Directory.EnumerateFileSystemEntries(directory)
            .FirstOrDefault(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));
    }
}
