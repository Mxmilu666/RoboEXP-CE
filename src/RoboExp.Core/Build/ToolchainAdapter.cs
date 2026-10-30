using RoboExp.Core.Install;
using RoboExp.Core.Model;

namespace RoboExp.Core.Build;

/// <summary>
/// Turns a JMCompiler.xml command (written for the bundled Windows GCC 4.8 / 10.3) into an argument list for the native
/// arm-none-eabi toolchain (GCC 15). Adaptations, all verified by building the vendor examples:
/// <list type="number">
/// <item>Paths: template '\' separators become '/', vendor paths are resolved case-insensitively
///   (e.g. E7RCU ships <c>include/</c> while its template says <c>Include\o</c>).</item>
/// <item>Tools: <c>?C\..\ARM\bin\arm-none-eabi-*</c> and <c>?C\..\ARM_v7\bin\arm-none-eabi-*</c> map to <see cref="ToolchainLocator.ArmNoneEabiBin"/>.</item>
/// <item>Compile steps (<c>gcc -c</c>) get <c>-fpermissive</c>: GCC 14 made implicit-int, implicit-function-declaration,
///   int-conversion and incompatible-pointer-types errors; vendor libraries rely on all of them.</item>
/// <item>Link steps get <c>-Wl,--no-warn-rwx-segments</c> (binutils ≥ 2.39 warns about every vendor linker script, which
///   the original would have shown as a compile failure since any output was treated as an error).</item>
/// <item>Per-type linker script overlays (<see cref="LinkerScriptPatches"/>), written to the cache and passed as the first -L.</item>
/// </list>
/// </summary>
internal sealed class ToolchainAdapter
{
    /// <summary>A textual fix applied to a copy of a vendor linker script.</summary>
    internal sealed record LinkerScriptPatch(string FileName, string Anchor, string InsertAfterAnchor, string Reason);

    /// <summary>
    /// Per-type linker script fixes. Types not listed link unmodified with GCC 15.
    /// E2RCU: <c>STM32_SEC_FLASH.ld</c> places no <c>.init</c>/<c>.fini</c>/<c>.ARM.exidx</c> input sections; newer newlib
    /// crt objects contain them, so ld creates orphan sections after .text and fails with "section .init overlaps .data".
    /// </summary>
    internal static IReadOnlyDictionary<SoftwareType, LinkerScriptPatch[]> LinkerScriptPatches { get; } = new Dictionary<SoftwareType, LinkerScriptPatch[]>
    {
        [SoftwareType.E2RCU] =
        [
            new("STM32_SEC_FLASH.ld", "*(.glue_7t)", "\r\n        KEEP(*(.init))\r\n        KEEP(*(.fini))\r\n        *(.ARM.exidx*)",
                "place .init/.fini/.ARM.exidx of GCC 15 newlib crt objects inside .text"),
        ],
    };

    private readonly VendorInstall _install;
    private readonly ToolchainLocator _toolchain;
    private readonly RobotType _type;

    public ToolchainAdapter(VendorInstall install, ToolchainLocator toolchain, RobotType type)
    {
        _install = install;
        _toolchain = toolchain;
        _type = type;
    }

    public static string OverlayDirectory(RobotType type) => Path.Combine(VendorInstall.CacheDirectory, type.Name, "ld");

    /// <summary>Adapts one tool invocation. Returns null with <paramref name="error"/> set when the tool cannot be provided.</summary>
    public IReadOnlyList<string>? Adapt(IReadOnlyList<string> rawArgs, out string? error)
    {
        error = null;
        if (rawArgs.Count == 0) { error = "empty command"; return null; }
        var args = rawArgs.Select(a => FixPathArgument(a.Replace('\\', '/'))).ToList();

        var tool = MapTool(args[0], out error);
        if (tool == null) return null;
        args[0] = tool;

        var toolName = Path.GetFileNameWithoutExtension(tool);
        if (toolName.EndsWith("-gcc", StringComparison.Ordinal))
        {
            if (args.Contains("-c"))
            {
                args.Insert(1, "-fpermissive");
            }
            else
            {
                args.Insert(1, "-Wl,--no-warn-rwx-segments");
                var overlay = PrepareOverlay(args);
                if (overlay != null)
                {
                    var firstL = args.FindIndex(1, a => a.StartsWith("-L", StringComparison.Ordinal));
                    args.Insert(firstL < 0 ? 1 : firstL, "-L" + overlay);
                }
            }
        }
        return args;
    }

    private string? MapTool(string tool, out string? error)
    {
        error = null;
        var name = Path.GetFileName(tool);
        var stem = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        if (stem.StartsWith("arm-none-eabi-", StringComparison.Ordinal))
        {
            if (!_toolchain.IsAvailable)
            {
                error = $"arm-none-eabi toolchain not found (set {ToolchainLocator.EnvVar} or install the Arm GNU Toolchain under ~/.local/toolchains)";
                return null;
            }
            var mapped = _toolchain.ToolPath(stem);
            if (File.Exists(mapped)) return mapped;
            error = $"{stem} not found in {_toolchain.ArmNoneEabiBin}";
            return null;
        }
        if (Path.IsPathRooted(tool) && IsUnder(tool, _install.Root))
        {
            error = $"bundled Windows tool '{name}' has no native replacement ({_type.Name} is not an arm-none-eabi target)";
            return null;
        }
        return tool;
    }

    private static bool IsUnder(string path, string root) =>
        Path.GetFullPath(path).StartsWith(root.TrimEnd('/', '\\') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || Path.GetFullPath(path).StartsWith(root.TrimEnd('/', '\\') + '/', StringComparison.OrdinalIgnoreCase);

    /// <summary>Normalizes rooted path arguments (and -I/-L values): collapse "..", fix case of existing segments.</summary>
    internal static string FixPathArgument(string arg)
    {
        foreach (var prefix in new[] { "-I", "-L", "-T" })
        {
            if (arg.Length > prefix.Length && arg.StartsWith(prefix, StringComparison.Ordinal) && Path.IsPathRooted(arg[prefix.Length..]))
                return prefix + ResolvePath(arg[prefix.Length..]);
        }
        return Path.IsPathRooted(arg) ? ResolvePath(arg) : arg;
    }

    /// <summary>Full path with each segment matched case-insensitively against existing entries when the exact one is missing.</summary>
    internal static string ResolvePath(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { return path; }
        if (Path.Exists(full)) return full;
        var root = Path.GetPathRoot(full)!;
        var current = root;
        var segments = full[root.Length..].Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var next = VendorInstall.FindChildIgnoreCase(current, segments[i]);
            if (next == null) return Path.Combine([current, .. segments[i..]]);
            current = next;
        }
        return current;
    }

    /// <summary>Writes the patched linker scripts of this type next to each other in the cache; returns that directory or null.</summary>
    private string? PrepareOverlay(List<string> args)
    {
        if (!LinkerScriptPatches.TryGetValue(_type.SoftwareType, out var patches)) return null;
        var libDirs = args.Where(a => a.StartsWith("-L", StringComparison.Ordinal) && a.Length > 2).Select(a => a[2..]).ToList();
        var dir = OverlayDirectory(_type);
        Directory.CreateDirectory(dir);
        foreach (var patch in patches)
        {
            var source = libDirs.Select(d => VendorInstall.FindChildIgnoreCase(d, patch.FileName)).FirstOrDefault(p => p != null)
                ?? throw new FileNotFoundException($"{patch.FileName} not found in linker search path for {_type.Name}");
            var text = File.ReadAllText(source, Gbk.Encoding);
            var at = text.IndexOf(patch.Anchor, StringComparison.Ordinal);
            if (at < 0) throw new InvalidDataException($"{source}: anchor '{patch.Anchor}' not found ({patch.Reason})");
            var patched = text.Insert(at + patch.Anchor.Length, patch.InsertAfterAnchor);
            var target = Path.Combine(dir, patch.FileName);
            if (!File.Exists(target) || File.ReadAllText(target, Gbk.Encoding) != patched)
            {
                var tmp = target + "." + Environment.ProcessId + "." + Environment.CurrentManagedThreadId + ".tmp";
                File.WriteAllText(tmp, patched, Gbk.Encoding);
                File.Move(tmp, target, overwrite: true);
            }
        }
        return dir;
    }
}
