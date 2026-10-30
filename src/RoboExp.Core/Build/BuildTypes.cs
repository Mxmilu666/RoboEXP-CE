using System.Text.RegularExpressions;

namespace RoboExp.Core.Build;

/// <summary>JMCompiler.xml command group: <c>&lt;main&gt;</c> for applications, <c>&lt;func&gt;</c> for subroutine projects.</summary>
public enum BuildGroup { Main, Func }

/// <summary>A located (or, for linker-level failures, unlocated: File "" / Line 0) compiler or linker message.</summary>
public sealed partial record CompilerDiagnostic(string File, int Line, int? Column, bool IsError, string Message)
{
    // gcc/cc1: "file:line[:col]: error|fatal error|warning|note: message" (file may carry a Windows drive letter).
    [GeneratedRegex(@"^(?<file>(?:[A-Za-z]:)?[^:\r\n]+?):(?<line>\d+):(?:(?<col>\d+):)?\s*(?<kind>fatal error|error|warning|note):\s*(?<msg>.*?)\s*$")]
    private static partial Regex GccLine();

    // ld: "file.c:12:(.text.main+0x4): undefined reference to `foo'" / "file.o:(.text+0x4): ..." (no debug info)
    [GeneratedRegex(@"^(?<file>(?:[A-Za-z]:)?[^:\r\n]+?):(?:(?<line>\d+):)?\s*\([^)]*\):\s*(?<msg>.*?)\s*$")]
    private static partial Regex LdLocated();

    // Linker/driver prefix: ".../arm-none-eabi-ld: ...", "collect2: error: ld returned 1 exit status"
    [GeneratedRegex(@"^(?<tool>(?:[A-Za-z]:)?(?:[^:\r\n]*[/\\-])?(?:ld|ld\.bfd|collect2|gcc|objcopy)(?:-[\d.]+)?(?:\.exe)?):\s+(?<rest>.*?)\s*$")]
    private static partial Regex ToolPrefix();

    [GeneratedRegex(@"^(?:(?<file>(?:[A-Za-z]:)?[^\r\n]*?):\s+)?(?<kind>fatal error|error|warning|note):\s*(?<msg>.*)$")]
    private static partial Regex KindPrefix();

    /// <summary>
    /// Parses one output line (raw or <see cref="Builder.Localize"/>d). Returns null for notes, context lines
    /// ("In function", source echo, carets) and anything that is not a diagnostic. <see cref="File"/> is the path as printed.
    /// </summary>
    public static CompilerDiagnostic? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        line = line.TrimEnd('\r', '\n');
        var m = GccLine().Match(line);
        if (m.Success)
        {
            var kind = m.Groups["kind"].Value;
            if (kind == "note") return null;
            int? col = m.Groups["col"].Success ? int.Parse(m.Groups["col"].Value) : null;
            return new CompilerDiagnostic(m.Groups["file"].Value, int.Parse(m.Groups["line"].Value), col, kind != "warning", m.Groups["msg"].Value);
        }
        var tool = ToolPrefix().Match(line);
        var rest = tool.Success ? tool.Groups["rest"].Value : line;
        if (rest.Contains("in function", StringComparison.OrdinalIgnoreCase) && rest.EndsWith(':')) return null;
        m = LdLocated().Match(rest);
        if (m.Success)
        {
            var msg = m.Groups["msg"].Value;
            var isError = !msg.StartsWith("warning", StringComparison.Ordinal);
            // Without a line number (objects lacking debug info, e.g. newlib members) the "file" is not a source path.
            return m.Groups["line"].Success
                ? new CompilerDiagnostic(m.Groups["file"].Value, int.Parse(m.Groups["line"].Value), null, isError, msg)
                : new CompilerDiagnostic("", 0, null, isError, rest);
        }
        if (!tool.Success || rest.Length == 0) return null;
        var k = KindPrefix().Match(rest);
        if (k.Success)
        {
            var kind = k.Groups["kind"].Value;
            if (kind == "note") return null;
            var msg = k.Groups["file"].Success ? k.Groups["file"].Value + ": " + k.Groups["msg"].Value : k.Groups["msg"].Value;
            return new CompilerDiagnostic("", 0, null, kind != "warning", msg);
        }
        return new CompilerDiagnostic("", 0, null, true, rest);
    }
}

public static class DiagnosticParser
{
    /// <summary>Parses all diagnostics of a tool log. Relative file paths are made absolute against <paramref name="workDir"/>.</summary>
    public static IReadOnlyList<CompilerDiagnostic> Parse(string log, string? workDir = null)
    {
        var list = new List<CompilerDiagnostic>();
        foreach (var line in log.Split('\n'))
        {
            var d = CompilerDiagnostic.TryParse(line);
            if (d == null) continue;
            if (d.File.Length > 0 && workDir != null)
            {
                try
                {
                    var native = d.File.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar);
                    d = d with { File = Path.GetFullPath(native, workDir) };
                }
                catch (ArgumentException) { }
            }
            list.Add(d);
        }
        return list;
    }
}

/// <summary>
/// Result of <see cref="Builder.BuildAsync"/>. <see cref="Log"/> is the concatenated tool output (what JMCompiler printed,
/// empty on a clean build); <see cref="LocalizedLog"/> is <see cref="Builder.Localize"/>d for <see cref="Builder.Language"/>.
/// </summary>
public sealed record BuildResult(bool Success, string Log, string LocalizedLog, IReadOnlyList<CompilerDiagnostic> Diagnostics, string? BinaryPath);

/// <summary>Flash region of a linker script MEMORY block (where the application image is linked).</summary>
public sealed record FlashRegion(ulong Origin, ulong Length, string LinkerScriptPath);
