using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using RoboExp.Core.Build;
using RoboExp.Core.Install;
using RoboExp.Core.Library;

namespace RoboExp.Cli;

/// <summary>
/// <c>roboexp build &lt;file.c&gt; --type T [--group main|func] [--lang en|chs|cht]</c> and
/// <c>roboexp build examples --type T|all [--verbose] [--keep]</c>.
/// </summary>
internal static partial class BuildCommands
{
    private const string Usage = """
        usage: roboexp build <file.c> --type <TYPE> [--group main|func] [--lang en|chs|cht]
               roboexp build examples --type <TYPE|all> [--verbose] [--keep]
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 0) return Error(Usage);
        string? type = null, group = "main", lang = "en";
        bool verbose = false, keep = false;
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--type" when i + 1 < args.Length: type = args[++i]; break;
                case "--group" when i + 1 < args.Length: group = args[++i].ToLowerInvariant(); break;
                case "--lang" when i + 1 < args.Length: lang = args[++i].ToLowerInvariant(); break;
                case "--verbose": verbose = true; break;
                case "--keep": keep = true; break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal)) return Error($"unknown option {args[i]}\n{Usage}");
                    positional.Add(args[i]);
                    break;
            }
        }
        if (positional.Count != 1 || type == null) return Error(Usage);
        try
        {
            var install = VendorInstall.Locate();
            var toolchain = ToolchainLocator.Default;
            if (!toolchain.IsAvailable)
            {
                var package = ToolchainCatalog.ForCurrentHost();
                if (package == null) return Error("No prebuilt Arm GNU Toolchain for this host; install arm-none-eabi-gcc and set ROBOEXP_ARM_GCC.");
                Console.Error.WriteLine($"First compilation: downloading Arm GNU Toolchain {package.Version} ({package.Size / 1e6:F0} MB); SHA-256 will be verified.");
                using var cancellation = new CancellationTokenSource();
                ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
                Console.CancelKeyPress += cancel;
                try
                {
                    var bin = new ToolchainInstaller().InstallAsync(package, null, new InstallProgress(), cancellation.Token).GetAwaiter().GetResult();
                    ToolchainLocator.Rediscover();
                    toolchain = new ToolchainLocator(bin);
                }
                finally { Console.CancelKeyPress -= cancel; }
            }
            var builder = new Builder(install, toolchain);
            if (positional[0] == "examples")
            {
                var types = type.Equals("all", StringComparison.OrdinalIgnoreCase) ? RobotType.Supported : [RobotType.Parse(type)];
                return BuildExamples(install, builder, types, verbose, keep);
            }
            builder.Language = lang switch
            {
                "chs" => UiLanguage.CHS,
                "cht" => UiLanguage.CHT,
                "en" or "default" => UiLanguage.Default,
                _ => throw new ArgumentException($"unknown language '{lang}'"),
            };
            var buildGroup = group switch
            {
                "main" => BuildGroup.Main,
                "func" => BuildGroup.Func,
                _ => throw new ArgumentException($"unknown group '{group}' (main|func)"),
            };
            return BuildOne(builder, install, positional[0], RobotType.Parse(type), buildGroup);
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException)
        {
            return Error(e.Message);
        }
    }

    private static int Error(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static int BuildOne(Builder builder, VendorInstall install, string source, RobotType type, BuildGroup group)
    {
        // Builds write outputs next to the source; never write into the vendor install (reference .bin files live there).
        var full = Path.GetFullPath(source);
        if (full.StartsWith(install.Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            var copyDir = Path.Combine(Path.GetTempPath(), "roboexp-build-" + Environment.ProcessId, Path.GetFileName(Path.GetDirectoryName(full)!));
            Directory.CreateDirectory(copyDir);
            foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(full)!))
                File.Copy(f, Path.Combine(copyDir, Path.GetFileName(f)), overwrite: true);
            Console.Error.WriteLine($"source is inside the RoboEXP install; building a copy in {copyDir}");
            source = Path.Combine(copyDir, Path.GetFileName(full));
        }
        var progress = new SyncProgress(s => Console.Error.Write(s));
        var result = builder.BuildAsync(source, type, group, progress).GetAwaiter().GetResult();
        if (result.LocalizedLog.Length > 0 && builder.Language != UiLanguage.Default)
            Console.WriteLine(result.LocalizedLog);
        foreach (var d in result.Diagnostics)
            Console.WriteLine($"{(d.IsError ? "error" : "warning")}: {(d.File.Length > 0 ? $"{d.File}:{d.Line}{(d.Column is { } c ? $":{c}" : "")}: " : "")}{d.Message}");
        Console.WriteLine(result.Success ? $"ok{(result.BinaryPath != null ? $": {result.BinaryPath} ({new FileInfo(result.BinaryPath).Length} bytes)" : "")}" : "failed");
        return result.Success ? 0 : 1;
    }

    /// <summary>Synchronous IProgress (Progress&lt;T&gt; posts to the thread pool and would reorder output).</summary>
    private sealed class SyncProgress(Action<string> action) : IProgress<string>
    {
        public void Report(string value) => action(value);
    }

    private sealed class InstallProgress : IProgress<ToolchainInstallProgress>
    {
        private long _last;
        private string? _phase;
        public void Report(ToolchainInstallProgress value)
        {
            var now = Environment.TickCount64;
            if (_phase == value.Phase && now - _last < 1000) return;
            _phase = value.Phase;
            _last = now;
            Console.Error.WriteLine(value.Phase == "download"
                ? $"Downloading compiler: {value.Done / 1e6:F1} / {value.Total / 1e6:F1} MB"
                : $"Compiler installation: {value.Phase}");
        }
    }

    private enum FailureKind { StaleSource, VendorLibrary, Toolchain }

    private static string Label(FailureKind kind) => kind switch
    {
        FailureKind.StaleSource => "stale-source",
        FailureKind.VendorLibrary => "vendor-lib",
        _ => "toolchain",
    };

    private sealed record ExampleResult(RobotType Type, string RelativePath, bool Success, long? BinSize, long? VendorBinSize, FailureKind Kind, string Reason, string FirstError);

    private static int BuildExamples(VendorInstall install, Builder builder, IReadOnlyList<RobotType> types, bool verbose, bool keep)
    {
        if (!ToolchainLocator.Default.IsAvailable)
            return Error($"arm-none-eabi toolchain not found (set {ToolchainLocator.EnvVar})");
        Console.WriteLine($"toolchain: {ToolchainLocator.Default.ArmNoneEabiBin}");
        var tempRoot = Path.Combine(Path.GetTempPath(), "roboexp-examples-" + Environment.ProcessId);
        var jobs = new List<(RobotType Type, string ExampleDir, string Rel)>();
        foreach (var type in types)
        {
            builder.GetLibrary(type); // generate stubs once, before the parallel builds
            var root = install.ExamplesDirectory(type);
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                if (VendorInstall.FindChildIgnoreCase(dir, Path.GetFileName(dir) + ".c") is { } c && File.Exists(c))
                    jobs.Add((type, dir, Path.GetRelativePath(root, dir)));
            }
        }

        var results = new ConcurrentBag<ExampleResult>();
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, job =>
        {
            var target = Path.Combine(tempRoot, job.Type.Name, job.Rel);
            CopyDirectory(job.ExampleDir, target);
            var name = Path.GetFileName(job.ExampleDir);
            var source = VendorInstall.FindChildIgnoreCase(target, name + ".c")!;
            var result = builder.BuildAsync(source, job.Type, BuildGroup.Main).GetAwaiter().GetResult();
            var vendorBin = VendorInstall.FindChildIgnoreCase(job.ExampleDir, name + ".bin");
            long? vendorSize = vendorBin != null ? new FileInfo(vendorBin).Length : null;
            if (result.Success)
            {
                results.Add(new ExampleResult(job.Type, job.Rel, true, new FileInfo(result.BinaryPath!).Length, vendorSize, default, "", ""));
            }
            else
            {
                var (kind, reason) = Classify(install, builder, job.Type, result, target);
                var first = result.Diagnostics.FirstOrDefault(d => d.IsError)?.Message ?? result.Log.Split('\n').FirstOrDefault(l => l.Length > 0) ?? "";
                results.Add(new ExampleResult(job.Type, job.Rel, false, null, vendorSize, kind, reason, first));
                if (verbose) Console.Error.WriteLine($"--- {job.Type.Name}/{job.Rel}\n{result.Log}");
            }
        });
        if (!keep) { try { Directory.Delete(tempRoot, recursive: true); } catch (IOException) { } }
        else Console.WriteLine($"build dirs kept in {tempRoot}");

        var toolchainFailures = 0;
        foreach (var r in results.Where(r => !r.Success).OrderBy(r => r.Type.Name, StringComparer.Ordinal).ThenBy(r => r.RelativePath, StringComparer.Ordinal))
            Console.WriteLine($"FAIL {r.Type.Name}/{r.RelativePath}: [{Label(r.Kind)}] {r.Reason}: {r.FirstError}");

        Console.WriteLine();
        Console.WriteLine($"{"type",-9} {"ok",4} {"fail",4} {"stale",5} {"vlib",4} {"tool",4}  {"bin/vendor median",17}  {"max bin",8} / flash   fits");
        foreach (var type in types)
        {
            var rs = results.Where(r => r.Type == type).ToList();
            if (rs.Count == 0) { Console.WriteLine($"{type.Name,-9} (no examples)"); continue; }
            var ok = rs.Where(r => r.Success).ToList();
            int Count(FailureKind k) => rs.Count(r => !r.Success && r.Kind == k);
            var tool = Count(FailureKind.Toolchain);
            toolchainFailures += tool;
            var ratios = ok.Where(r => r.VendorBinSize is > 0).Select(r => (double)r.BinSize!.Value / r.VendorBinSize!.Value).Order().ToList();
            var median = ratios.Count == 0 ? double.NaN : ratios.Count % 2 == 1 ? ratios[ratios.Count / 2] : (ratios[ratios.Count / 2 - 1] + ratios[ratios.Count / 2]) / 2;
            var flash = builder.GetFlashRegion(type);
            var maxBin = ok.Count == 0 ? 0 : ok.Max(r => r.BinSize!.Value);
            var over = flash == null ? 0 : ok.Count(r => (ulong)r.BinSize!.Value > flash.Length);
            var fits = flash == null ? "no FLASH region" : over == 0 ? $"yes ({100.0 * maxBin / flash.Length:F0}%)" : $"NO: {over} image(s) exceed";
            Console.WriteLine($"{type.Name,-9} {ok.Count,4} {rs.Count - ok.Count,4} {Count(FailureKind.StaleSource),5} {Count(FailureKind.VendorLibrary),4} {tool,4}  {median,8:F2} (n={ratios.Count,3})  {maxBin,8} / {(flash == null ? "?" : $"{flash.Length / 1024}K @0x{flash.Origin:X8}"),-16}  {fits}");
        }
        var total = results.Count;
        var okTotal = results.Count(r => r.Success);
        var vlib = results.Count(r => !r.Success && r.Kind == FailureKind.VendorLibrary);
        Console.WriteLine($"total: {total} examples, ok {okTotal}, fail {total - okTotal} (stale-source {total - okTotal - toolchainFailures - vlib}, vendor-lib {vlib}, toolchain {toolchainFailures})");
        return toolchainFailures == 0 ? 0 : 1;
    }

    [GeneratedRegex(@"undefined reference to [`'‘](?<sym>[^'’`]+)['’]")]
    private static partial Regex UndefinedReference();

    [GeneratedRegex(@"^(?<file>[^:]+?):\d+(?::\d+)?: fatal error: (?<inc>[^:]+): No such file or directory")]
    private static partial Regex MissingInclude();

    [GeneratedRegex(@"undeclared|conflicting types|too few arguments|too many arguments|unknown type name|redefinition of|has no member named")]
    private static partial Regex VersionIndependentError();

    [GeneratedRegex(@"unknown type name '_\w+_'")]
    private static partial Regex UnknownHardwareType();

    /// <summary>
    /// Separates failures caused by the example sources being out of date with the shipped library (the original IDE regenerates
    /// HardwareInfo.c and the code from the .rcu before compiling) from anything attributable to the native toolchain.
    /// </summary>
    private static (FailureKind, string) Classify(VendorInstall install, Builder builder, RobotType type, BuildResult result, string projectDir)
    {
        var lib = builder.GetLibrary(type);
        var includeDir = install.IncludeDirectory(type);
        var lines = result.Log.Split('\n');
        foreach (var line in lines)
        {
            var m = MissingInclude().Match(line);
            if (!m.Success) continue;
            var inc = m.Groups["inc"].Value.Trim();
            if (inc.Equals("HardwareInfo.c", StringComparison.OrdinalIgnoreCase))
                return (FailureKind.StaleSource, "missing HardwareInfo.c (generated from the .rcu by the IDE)");
            if (inc.EndsWith(".h", StringComparison.OrdinalIgnoreCase) && lib.Find(Path.GetFileNameWithoutExtension(inc)) == null)
                return (FailureKind.StaleSource, $"missing lib header {inc} (function not in this type's JMlib.xml)");
            // A project-local subroutine source the example does not ship.
            if (!File.Exists(Path.Combine(projectDir, inc)) && !inc.Equals(SystemLibrary.LibSourceFileName(type), StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(inc).Equals(".c", StringComparison.OrdinalIgnoreCase))
                return (FailureKind.StaleSource, $"missing project file {inc}");
        }
        var undefined = lines.Select(l => UndefinedReference().Match(l)).Where(m => m.Success).Select(m => m.Groups["sym"].Value).Distinct().ToList();
        if (undefined.Count > 0)
        {
            var reasons = new List<string>();
            // BaseView.CreateHardwareInfoFile appends "void task1(){}" for these types; the shipped HardwareInfo.c predates that.
            if (type.Name is "X4RCU" or "E6RCU" or "E7RCU" && undefined.Remove("task1"))
                reasons.Add("undefined task1 (stub the IDE writes into the regenerated HardwareInfo.c)");
            var otherInit = undefined.Where(s => s.EndsWith("_Init", StringComparison.Ordinal) && !s.Equals(type.Name + "_Init", StringComparison.Ordinal)).ToList();
            if (otherInit.Count > 0) reasons.Add($"undefined {string.Join(", ", otherInit)} (example written for another controller)");
            var rest = undefined.Except(otherInit).ToList();
            // Listed in JMlib.xml is not enough: some types' JMLib.c lack the implementation (E7RCU SetWifiUdpMode).
            var libSource = LibSourceText(includeDir, type);
            var notInLibrary = rest.Where(s => lib.Find(s) == null || !Regex.IsMatch(libSource, @"\b" + Regex.Escape(s) + @"\s*\([^;{]*\)\s*\{")).ToList();
            if (notInLibrary.Count > 0) reasons.Add($"undefined {string.Join(", ", notInLibrary)} (not provided by this type's library)");
            if (reasons.Count > 0 && notInLibrary.Count == rest.Count) return (FailureKind.StaleSource, string.Join("; ", reasons));
        }
        var errors = result.Diagnostics.Where(d => d.IsError).ToList();
        // Compile errors in the example's own source (renamed functions/constants) are stale source.
        if (errors.Count > 0 && errors.All(d => d.File.Length > 0 && IsUnder(d.File, projectDir) && !d.Message.Contains("undefined reference")))
            return (FailureKind.StaleSource, "compile error in example source");
        // Errors inside the shipped library sources (JMLib.c sections enabled by the example's stub headers) that every GCC version
        // reports as hard errors: the library changed after the example (and its vendor .bin) was produced.
        if (errors.Count > 0 && errors.All(d => d.File.Length > 0 && IsUnder(d.File, includeDir) && VersionIndependentError().IsMatch(d.Message)))
        {
            if (errors.Any(d => UnknownHardwareType().IsMatch(d.Message)))
                return (FailureKind.StaleSource, "hardware type typedef missing (generated into HardwareInfo.c by the IDE)");
            return (FailureKind.VendorLibrary, "vendor library source does not compile with this example in any GCC version");
        }
        return (FailureKind.Toolchain, "unclassified");
    }

    private static readonly ConcurrentDictionary<string, string> LibSources = new();

    private static string LibSourceText(string includeDir, RobotType type) => LibSources.GetOrAdd(type.Name, _ =>
        VendorInstall.FindChildIgnoreCase(includeDir, SystemLibrary.LibSourceFileName(type)) is { } p
            ? File.ReadAllText(p, System.Text.Encoding.Latin1)
            : "");

    private static bool IsUnder(string path, string dir) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            var ext = Path.GetExtension(file);
            if (ext.Equals(".bin", StringComparison.OrdinalIgnoreCase) || ext.Equals(".o", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var dir in Directory.EnumerateDirectories(from))
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}
