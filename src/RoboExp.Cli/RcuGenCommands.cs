using System.Collections.Concurrent;
using System.Text;
using RoboExp.Core.Build;
using RoboExp.Core.CodeGen;
using RoboExp.Core.Flow;
using RoboExp.Core.Install;
using RoboExp.Core.Model;
using RoboExp.Core.Project;
using RoboExp.Core.Rcu;

namespace RoboExp.Cli;

/// <summary><c>roboexp rcu gen|gencheck|compile</c>.</summary>
internal static partial class RcuCommands
{
    private static (List<string> Positional, string? Type, int MaxDiffs, bool Verbose) ParseGenArgs(string[] args)
    {
        var positional = new List<string>();
        string? type = null;
        var max = 10;
        var verbose = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--type" when i + 1 < args.Length: type = args[++i]; break;
                case "--max-diffs" when i + 1 < args.Length: max = int.Parse(args[++i]); break;
                case "--verbose": verbose = true; break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal)) throw new FormatException($"unknown option {args[i]}\n{Usage}");
                    positional.Add(args[i]);
                    break;
            }
        }
        return (positional, type, max, verbose);
    }

    /// <summary>Type from an explicit option, else the <c>Robots/&lt;TYPE&gt;/</c> path segment, else the stored SoftwareType.</summary>
    private static RobotType? TypeFor(string path, string? option)
    {
        if (option != null) return RobotType.Parse(option);
        var parts = Path.GetFullPath(path).Split(Path.DirectorySeparatorChar);
        var i = Array.FindLastIndex(parts, p => p.Equals("Robots", StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 1 < parts.Length)
            return RobotType.Supported.FirstOrDefault(t => t.Name.Equals(parts[i + 1], StringComparison.OrdinalIgnoreCase));
        return null;
    }

    private static int Gen(string[] args)
    {
        var (pos, type, _, _) = ParseGenArgs(args);
        if (pos.Count != 1) return Error(Usage);
        var session = ProjectSession.Open(pos[0], VendorInstall.Locate(), TypeFor(pos[0], type));
        var text = session.Regenerate();
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
        stdout.Write(text);
        return 0;
    }

    private sealed record GenCheckResult(string Path, string Type, string Status, string? Detail);

    private static int GenCheck(string[] args)
    {
        var (pos, typeOption, maxDiffs, verboseMatches) = ParseGenArgs(args);
        if (pos.Count != 1 || !Directory.Exists(pos[0])) return Error(Usage);
        var dir = pos[0];
        var install = VendorInstall.Locate();
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".rcu", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList();
        var results = new ConcurrentBag<GenCheckResult>();
        Parallel.ForEach(files, f => results.Add(GenCheckFile(f, typeOption, install)));

        Console.WriteLine($"{"type",-9} {"flow",5} {"match",6} {"explained",9} {"differ",6} {"code-proj",9} {"skipped",7}");
        void Row(string name, IEnumerable<GenCheckResult> rows)
        {
            var list = rows.ToList();
            int C(string s) => list.Count(r => r.Status == s);
            Console.WriteLine($"{name,-9} {C("match") + C("explained") + C("differ"),5} {C("match"),6} {C("explained"),9} {C("differ"),6} {C("code"),9} {C("skipped"),7}");
        }
        foreach (var g in results.GroupBy(r => r.Type).OrderBy(g => g.Key, StringComparer.Ordinal)) Row(g.Key, g);
        var all = results.ToList();
        Row("total", all);
        Console.WriteLine("explained mismatch classes (stored AllCode written by another release/type/library list):");
        foreach (var g in all.Where(r => r.Status == "explained").GroupBy(r => r.Detail).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Count(),4} {g.Key}");
        var shown = 0;
        foreach (var r in all.Where(r => r.Status is "differ" or "skipped" || (verboseMatches && r.Status == "match")).OrderBy(r => r.Path, StringComparer.Ordinal))
        {
            if (shown++ == maxDiffs) break;
            Console.WriteLine($"{r.Status.ToUpperInvariant()} {Path.GetRelativePath(dir, r.Path)}: {r.Detail}");
        }
        return all.Any(r => r.Status == "differ") ? 1 : 0;
    }

    private static GenCheckResult GenCheckFile(string path, string? typeOption, VendorInstall install)
    {
        RcuFile file;
        try { file = RcuReader.Open(path); }
        catch (InvalidDataException ex) { return new GenCheckResult(path, "?", "skipped", "read: " + ex.Message); }
        var type = TypeFor(path, typeOption)
                   ?? RobotType.Supported.FirstOrDefault(t => t.SoftwareType == file.ProjectData?.ProjectInfo?.SoftwareType);
        if (type == null) return new GenCheckResult(path, "?", "skipped", "unsupported type " + file.ProjectData?.ProjectInfo?.SoftwareType);
        if (file.FlowData == null) return new GenCheckResult(path, type.Name, "code", null);
        var stored = file.ProjectData?.CodeFrameData?.AllCode ?? "";
        var session = ProjectSession.Create(file, path, install, type);
        string generated;
        try { generated = session.Regenerate(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new GenCheckResult(path, type.Name, "skipped", "generate: " + ex.Message); }
        if (generated == stored) return new GenCheckResult(path, type.Name, "match", null);
        if (stored.Length == 0)
            return new GenCheckResult(path, type.Name, "explained", "stored-AllCode-empty (never regenerated before save)");
        // Explain the difference by the documented ways older releases / other settings produced different text.
        var authoredType = AuthoredType(stored) ?? type.SoftwareType;
        var baseText = generated;
        var labels = new List<string>();
        if (authoredType != type.SoftwareType)
        {
            var again = RcuReader.Open(path);
            var g = new FlowGraph(again.FlowData!.DesignSurfaceData?.DrawingObjects);
            baseText = CodeGenerator.Generate(again.ProjectData!, g, session.CodeGenOptions with { Type = authoredType }).Text;
            labels.Add("authored-as-" + authoredType);
        }
        var candidate = baseText;
        if (candidate != stored && !stored.Contains("#include <stm32", StringComparison.Ordinal))
        {
            candidate = LegacyLayout(candidate, file.ProjectData!);
            labels.Add("legacy-layout");
        }
        if (candidate != stored && candidate.Contains("#include <stm32h7xx_hal.h>") && stored.Contains("#include <stm32h743xx.h>"))
        {
            candidate = candidate.Replace("#include <stm32h7xx_hal.h>", "#include <stm32h743xx.h>");
            labels.Add("old-E7-chip-header");
        }
        if (candidate != stored && candidate.Contains("#include \"JMLib.c\"\r\n") && !stored.Contains("#include \"JMLib.c\""))
        {
            candidate = candidate.Replace("#include \"JMLib.c\"\r\n", "");
            labels.Add("no-JMLib-include");
        }
        if (candidate != stored && candidate.Contains("    while(1);\r\n}") && stored.Contains("    return 1;\r\n}"))
        {
            candidate = candidate.Replace("    while(1);\r\n}", "    return 1;\r\n}");
            labels.Add("return-1-epilogue");
        }
        if (candidate != stored && AuthoredType(stored) == null
            && System.Text.RegularExpressions.Regex.Match(candidate, @"\r\n {3,4}\w+_Init\(\);\r\n") is { Success: true } init)
        {
            candidate = candidate.Remove(init.Index, init.Length - 2);
            labels.Add("no-Init-call (release before <TYPE>_Init)");
        }
        if (candidate != stored && SwapIncludeKinds(candidate, stored) is { } swapped && swapped != candidate)
        {
            candidate = swapped;
            labels.Add("different-system-library-list");
        }
        if (candidate == stored) return new GenCheckResult(path, type.Name, "explained", string.Join("+", labels));
        return new GenCheckResult(path, type.Name, "differ", (labels.Count > 0 ? "[" + string.Join("+", labels) + "] " : "") + FirstDifference(stored, candidate));
    }

    /// <summary>The controller named by the stored <c>&lt;TYPE&gt;_Init();</c> line (IQRobot_Init → IQ2Robot).</summary>
    private static SoftwareType? AuthoredType(string stored)
    {
        var m = System.Text.RegularExpressions.Regex.Match(stored, @"\r\n {3,4}(\w+)_Init\(\);\r\n");
        if (!m.Success) return null;
        var name = m.Groups[1].Value == "IQRobot" ? "IQ2Robot" : m.Groups[1].Value;
        return Enum.TryParse<SoftwareType>(name, out var t) ? t : null;
    }

    /// <summary>Rewrites <c>#include &lt;X.h&gt;</c> ↔ <c>#include "X.c"</c> lines to the form the stored text uses for X.</summary>
    private static string? SwapIncludeKinds(string candidate, string stored)
    {
        var storedLines = new HashSet<string>(stored.Split("\r\n"), StringComparer.Ordinal);
        var lines = candidate.Split("\r\n");
        for (var i = 0; i < lines.Length; i++)
        {
            var m = System.Text.RegularExpressions.Regex.Match(lines[i], "^#include (?:<(\\w+)\\.h>|\"(\\w+)\\.c\")$");
            if (!m.Success || storedLines.Contains(lines[i])) continue;
            var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            var other = m.Groups[1].Success ? "#include \"" + name + ".c\"" : "#include <" + name + ".h>";
            if (storedLines.Contains(other)) lines[i] = other;
        }
        return string.Join("\r\n", lines);
    }

    /// <summary>
    /// The file layout of the RoboExp releases that authored most vendor examples: no chip header, and the #include block
    /// comes before the Global block (then the blank line); everything else identical. Used only to classify stored AllCode.
    /// </summary>
    internal static string LegacyLayout(string generated, ProjectData data)
    {
        var lines = generated.Split("\r\n").ToList();
        var at = data.ProjectInfo?.ProjectType == ProjectType.FlowFunc ? 2 : 0;
        if (at < lines.Count && lines[at].StartsWith("#include <stm32", StringComparison.Ordinal)) lines.RemoveAt(at);
        var globalCount = CodeGenerator.SplitCrlf(data.CodeFrameData?.Global ?? "").Count;
        var blank = at + globalCount;
        if (blank >= lines.Count || lines[blank] != "") return generated;
        var globals = lines.GetRange(at, globalCount);
        lines.RemoveRange(at, globalCount + 1);
        var end = at;
        while (end < lines.Count && lines[end].StartsWith("#include ", StringComparison.Ordinal)) end++;
        lines.InsertRange(end, [.. globals, ""]);
        return string.Join("\r\n", lines);
    }

    internal static string FirstDifference(string stored, string generated)
    {
        var a = stored.Split("\r\n");
        var b = generated.Split("\r\n");
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : "<eof>";
            var y = i < b.Length ? b[i] : "<eof>";
            if (x != y) return $"line {i + 1}: stored [{x}] generated [{y}] (stored {a.Length} lines, generated {b.Length})";
        }
        return "line-ending difference";
    }

    private static int Compile(string[] args)
    {
        var (pos, typeOption, _, verbose) = ParseGenArgs(args);
        if (pos.Count != 1) return Error(Usage);
        var install = VendorInstall.Locate();
        var builder = new Builder(install, ToolchainLocator.Default);
        var target = pos[0];
        if (System.IO.File.Exists(target))
        {
            var r = CompileOne(target, typeOption, install, builder);
            Console.WriteLine($"{r.Status} {target}{(r.Detail != null ? ": " + r.Detail : "")}");
            if (r.Log.Length > 0) Console.WriteLine(r.Log.TrimEnd());
            return r.Status == "ok" ? 0 : 1;
        }
        if (!Directory.Exists(target)) return Error($"not found: {target}");
        var files = Directory.EnumerateFiles(target, "*.rcu", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        var results = new ConcurrentBag<(string Path, string Type, CompileRow Result)>();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, f =>
        {
            var t = TypeFor(f, typeOption);
            results.Add((f, t?.Name ?? "?", CompileOne(f, typeOption, install, builder)));
        });
        Console.WriteLine($"{"type",-9} {"total",5} {"ok",5} {"fail",5} {"precheck",8} {"skipped",7}");
        foreach (var g in results.GroupBy(r => r.Type).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            int C(string s) => g.Count(r => r.Result.Status == s);
            Console.WriteLine($"{g.Key,-9} {g.Count(),5} {C("ok"),5} {C("fail"),5} {C("precheck"),8} {C("skipped"),7}");
        }
        foreach (var r in results.Where(r => r.Result.Status != "ok").OrderBy(r => r.Path, StringComparer.Ordinal))
        {
            Console.WriteLine($"{r.Result.Status.ToUpperInvariant()} {Path.GetRelativePath(target, r.Path)}: {r.Result.Detail}");
            if (verbose && r.Result.Log.Length > 0) Console.WriteLine(r.Result.Log.TrimEnd());
        }
        return results.All(r => r.Result.Status == "ok") ? 0 : 1;
    }

    private sealed record CompileRow(string Status, string? Detail, string Log);

    /// <summary>Copies the project directory to a temp dir (sub projects travel with it), opens the copy, compiles.</summary>
    private static CompileRow CompileOne(string rcuPath, string? typeOption, VendorInstall install, Builder builder)
    {
        var temp = Path.Combine(Path.GetTempPath(), "roboexp-compile-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var type = TypeFor(rcuPath, typeOption);
            CopyDirectory(Path.GetDirectoryName(Path.GetFullPath(rcuPath))!, temp);
            var copy = Path.Combine(temp, Path.GetFileName(rcuPath));
            ProjectSession session;
            try { session = ProjectSession.Open(copy, install, type); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { return new CompileRow("skipped", ex.Message, ""); }
            var outcome = session.CompileAsync(builder).GetAwaiter().GetResult();
            var checks = string.Join("\n", outcome.Checks.Issues.Select(i => i.Text));
            if (outcome.Build == null) return new CompileRow("precheck", checks, checks);
            var first = outcome.Diagnostics.FirstOrDefault(d => d.Diagnostic.IsError);
            var detail = first == null ? null
                : $"{Path.GetFileName(first.Diagnostic.File)}:{first.Diagnostic.Line}: {first.Diagnostic.Message}"
                  + (first.Object != null ? $" [icon {FlowGraph.IconTypeOf(first.Object)}]" : "");
            return new CompileRow(outcome.Success ? "ok" : "fail", detail, (checks.Length > 0 ? checks + "\n" : "") + outcome.Build.Log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            return new CompileRow("fail", ex.GetType().Name + ": " + ex.Message, "");
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src)) System.IO.File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
    }
}
