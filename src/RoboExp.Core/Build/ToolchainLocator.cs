using System.Text.RegularExpressions;
using RoboExp.Core.Install;

namespace RoboExp.Core.Build;

/// <summary>
/// Finds the native arm-none-eabi toolchain replacing the bundled Windows <c>ARM\bin</c> (GCC 4.8) and <c>ARM_v7\bin</c> (GCC 10.3).
/// Order: <c>$ROBOEXP_ARM_GCC</c> (bin directory or path of arm-none-eabi-gcc), newest
/// <c>arm-gnu-toolchain-*/bin</c> under the application's <c>toolchains</c> or macOS <c>Contents/Resources/toolchains</c>, then
/// <see cref="DataDirectory.ToolchainsRoot"/> (where <see cref="ToolchainInstaller"/> installs),
/// newest <c>~/.local/toolchains/arm-gnu-toolchain-*/bin</c>, then <c>PATH</c>.
/// <see cref="Default"/> is discovered once; <see cref="Rediscover"/> refreshes it (e.g. after an install).
/// </summary>
public sealed partial class ToolchainLocator
{
    public const string EnvVar = "ROBOEXP_ARM_GCC";

    private static ToolchainLocator? _default;

    /// <param name="armNoneEabiBin">Directory containing arm-none-eabi-gcc/objcopy (or the path of arm-none-eabi-gcc itself).</param>
    /// <exception cref="DirectoryNotFoundException">The directory does not contain arm-none-eabi-gcc.</exception>
    public ToolchainLocator(string armNoneEabiBin)
    {
        var full = Path.GetFullPath(armNoneEabiBin);
        if (File.Exists(full)) full = Path.GetDirectoryName(full)!;
        ArmNoneEabiBin = full;
        if (!IsAvailable) throw new DirectoryNotFoundException($"arm-none-eabi-gcc{ExeSuffix} not found in {full}");
    }

    private ToolchainLocator(string bin, bool _) => ArmNoneEabiBin = bin;

    public static ToolchainLocator Default => Volatile.Read(ref _default) ?? Rediscover();

    /// <summary>Runs discovery again (ignoring the cached <see cref="Default"/>), stores the result as <see cref="Default"/> and returns it.</summary>
    public static ToolchainLocator Rediscover()
    {
        var found = Discover();
        Volatile.Write(ref _default, found);
        return found;
    }

    /// <summary>Toolchain bin directory; empty when nothing was found (see <see cref="IsAvailable"/>).</summary>
    public string ArmNoneEabiBin { get; }

    public bool IsAvailable => ArmNoneEabiBin.Length > 0 && File.Exists(ToolPath("arm-none-eabi-gcc"));

    /// <summary>Full path of a tool (e.g. "arm-none-eabi-objcopy") in <see cref="ArmNoneEabiBin"/>.</summary>
    public string ToolPath(string toolName) => Path.Combine(ArmNoneEabiBin, toolName + ExeSuffix);

    internal static string ExeSuffix => OperatingSystem.IsWindows() ? ".exe" : "";

    [GeneratedRegex(@"arm-gnu-toolchain-(\d+)\.(\d+)(?:\.rel(\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();

    private static ToolchainLocator Discover() => Discover(
        AppContext.BaseDirectory,
        DataDirectory.ToolchainsRoot,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "toolchains"),
        Environment.GetEnvironmentVariable(EnvVar),
        Environment.GetEnvironmentVariable("PATH"));

    internal static ToolchainLocator Discover(string baseDirectory, string userToolchainsRoot,
        string homeToolchainsRoot, string? env, string? path)
    {
        if (!string.IsNullOrEmpty(env))
            return new ToolchainLocator(File.Exists(env) ? Path.GetDirectoryName(Path.GetFullPath(env))! : Path.GetFullPath(env), false);

        var resourcesRoot = Path.Combine(baseDirectory, "..", "Resources", "toolchains");
        foreach (var root in new[] { Path.Combine(baseDirectory, "toolchains"), resourcesRoot, userToolchainsRoot, homeToolchainsRoot })
        {
            if (NewestIn(root) is { } bin) return new ToolchainLocator(bin);
        }

        foreach (var dir in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(dir, "arm-none-eabi-gcc" + ExeSuffix))) return new ToolchainLocator(Path.GetFullPath(dir));
        }
        return new ToolchainLocator("", false);
    }

    private static string? NewestIn(string root)
    {
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateDirectories(root, "arm-gnu-toolchain-*")
            .Select(d => (Dir: d, Bin: Path.Combine(d, "bin")))
            .Where(x => File.Exists(Path.Combine(x.Bin, "arm-none-eabi-gcc" + ExeSuffix)))
            .OrderByDescending(x => VersionKey(Path.GetFileName(x.Dir)))
            .ThenByDescending(x => Path.GetFileName(x.Dir), StringComparer.Ordinal)
            .FirstOrDefault().Bin;
    }

    internal static (int, int, int) VersionKey(string dirName)
    {
        var m = VersionPattern().Match(dirName);
        if (!m.Success) return (-1, -1, -1);
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }
}
