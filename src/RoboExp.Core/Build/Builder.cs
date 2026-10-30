using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RoboExp.Core.Install;
using RoboExp.Core.Library;

namespace RoboExp.Core.Build;

/// <summary>
/// Port of JMCompiler.exe as driven by MainForm.Compile: expands the <c>Compiler/JMCompiler.xml</c> templates of the robot type
/// and runs them with the native toolchain (see <see cref="ToolchainAdapter"/> for the adaptations).
/// Include directories are the robot's Include dir, the project dir and the generated library stub dir; the lib dir is the Include dir.
/// </summary>
public sealed partial class Builder
{
    private readonly VendorInstall _install;
    private readonly ToolchainLocator _toolchain;
    private readonly Lazy<JMCompilerConfig> _config;
    private readonly ConcurrentDictionary<RobotType, Lazy<SystemLibrary>> _libraries = new();

    public Builder(VendorInstall install, ToolchainLocator toolchain)
    {
        _install = install;
        _toolchain = toolchain;
        _config = new Lazy<JMCompilerConfig>(() => JMCompilerConfig.Load(install.CompilerXmlPath));
    }

    /// <summary>Language used for <see cref="BuildResult.LocalizedLog"/>.</summary>
    public UiLanguage Language { get; set; } = UiLanguage.Default;

    public string CompilerDirectory => Path.Combine(_install.Root, "Compiler");

    /// <summary>The system library of <paramref name="type"/>; loading it generates the stub headers (cached per builder).</summary>
    public SystemLibrary GetLibrary(RobotType type) =>
        _libraries.GetOrAdd(type, t => new Lazy<SystemLibrary>(() => SystemLibrary.Load(_install, t))).Value;

    /// <summary>The template context MainForm.Compile passes for a source file.</summary>
    public TemplateContext ContextFor(string sourceCPath, RobotType type)
    {
        var source = Path.GetFullPath(sourceCPath);
        var projectDir = Path.GetDirectoryName(source)!;
        var include = _install.IncludeDirectory(type);
        var stubs = GetLibrary(type).StubIncludeDirectory;
        return new TemplateContext(source, projectDir, include + "*" + projectDir + "*" + stubs, include, CompilerDirectory);
    }

    public async Task<BuildResult> BuildAsync(string sourceCPath, RobotType type, BuildGroup group, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var output = new StringBuilder();
        var extraDiagnostics = new List<CompilerDiagnostic>();
        void AddError(string message)
        {
            output.Append("error: ").Append(message).Append('\n');
            extraDiagnostics.Add(new CompilerDiagnostic("", 0, null, true, message));
            log?.Report("error: " + message + "\n");
        }
        BuildResult Fail(string message)
        {
            AddError(message);
            return Finish(false, null);
        }
        BuildResult Finish(bool success, string? bin)
        {
            var text = output.ToString();
            var workDir = Path.GetDirectoryName(Path.GetFullPath(sourceCPath));
            var diagnostics = DiagnosticParser.Parse(text, workDir).Concat(extraDiagnostics).ToList();
            return new BuildResult(success, text, Localize(text, Language), diagnostics, bin);
        }

        if (!File.Exists(sourceCPath)) return Fail($"source file not found: {sourceCPath}");
        var commands = _config.Value.GetCommands(type.Name, JMCompilerConfig.GroupName(group));
        if (commands == null) return Fail($"Command group \"{JMCompilerConfig.GroupName(group)}\" of \"{type.Name}\" missing in {_install.CompilerXmlPath}");

        TemplateContext ctx;
        try { ctx = ContextFor(sourceCPath, type); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException) { return Fail(e.Message); }

        var adapter = new ToolchainAdapter(_install, _toolchain, type);
        var workDir = JMCompilerTemplate.NormalizeWorkDir(ctx.WorkDir, ctx.SourcePath);
        var failed = false;
        foreach (var template in commands)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(template)) continue;
            var step = JMCompilerTemplate.ToStep(JMCompilerTemplate.Expand(template, ctx));
            switch (step)
            {
                case BuildStep.Delete d:
                    // Intermediates are removed even after a failed step so stale objects never reach a later "?O" link.
                    try { var p = NativePath(d.Path); if (File.Exists(p)) File.Delete(p); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    break;
                case BuildStep.Test t when !failed:
                    if (!File.Exists(NativePath(t.Path))) failed = true;
                    break;
                case BuildStep.Run r when !failed:
                    IReadOnlyList<string>? args;
                    string? error;
                    try { args = adapter.Adapt(JMCompilerTemplate.SplitCommandLine(r.CommandLine), out error); }
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { args = null; error = e.Message; }
                    if (args == null) { AddError(error!); failed = true; break; }
                    log?.Report("> " + string.Join(' ', args.Select(QuoteForDisplay)) + "\n");
                    var (exit, text) = await RunToolAsync(args, workDir, ct).ConfigureAwait(false);
                    output.Append(text);
                    if (text.Length > 0) log?.Report(text);
                    if (exit != 0) failed = true;
                    break;
            }
        }

        if (failed) return Finish(false, null);
        if (group == BuildGroup.Func) return Finish(true, null);
        var bin = Path.Combine(Path.GetDirectoryName(ctx.SourcePath)!, Path.GetFileNameWithoutExtension(ctx.SourcePath) + ".bin");
        return File.Exists(bin) ? Finish(true, bin) : Fail($"{bin} was not produced");
    }

    private static string NativePath(string templatePath) => Path.GetFullPath(templatePath.Replace('\\', '/'));

    private static string QuoteForDisplay(string a) => a.Contains(' ') ? "\"" + a + "\"" : a;

    private static async Task<(int ExitCode, string Output)> RunToolAsync(IReadOnlyList<string> args, string workDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(args[0])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
        };
        if (workDir.Length > 0) psi.WorkingDirectory = workDir;
        for (var i = 1; i < args.Count; i++) psi.ArgumentList.Add(args[i]);
        // ASCII quotes in diagnostics (matches the original GCC output that Localize() and users expect).
        psi.Environment["LC_ALL"] = "C";

        using var process = new Process { StartInfo = psi };
        try { process.Start(); }
        catch (Win32Exception e) { return (-1, $"error: cannot run {args[0]}: {e.Message}\n"); }

        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        var outTask = process.StandardOutput.BaseStream.CopyToAsync(stdout, CancellationToken.None);
        var errTask = process.StandardError.BaseStream.CopyToAsync(stderr, CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
        var text = Gbk.DecodeTolerant(stdout.GetBuffer().AsSpan(0, (int)stdout.Length))
                 + Gbk.DecodeTolerant(stderr.GetBuffer().AsSpan(0, (int)stderr.Length));
        return (process.ExitCode, text);
    }

    /// <summary>
    /// The Chinese hints MainForm.Compile adds to gcc output for the CHS/CHT cultures (same text for both); Default is unchanged.
    /// </summary>
    public static string Localize(string gccOutput, UiLanguage lang)
    {
        if (lang == UiLanguage.Default) return gccOutput;
        return gccOutput
            .Replace("too few arguments to function", "(该模块参数没有填写完整，可能由更新模块参数引起)too few arguments to function")
            .Replace("undeclared (first use in this function)", "没有声明，请检查所使用的名称、大小写是否正确 undeclared (first use in this function)")
            .Replace("undefined reference to", "未定义子程序,可能缺少引用子程序文件 undefined reference to")
            .Replace("too many arguments to function", "函数调用时参数过多 too many arguments to function")
            .Replace("declared here", "declared here 查看此处声明")
            .Replace("each undeclared identifier is reported only once for each function it appears in", "对于每个出现的函数，每个未声明的标识符只报告一次错误")
            .Replace("No such file or directory", "没有找到文件或目录 No such file or directory")
            .Replace("In function", "在函数中")
            .Replace("expected ';' before", "前面缺少;符号");
    }

    /// <summary>
    /// The FLASH region the type's <c>&lt;main&gt;</c> link step targets (its <c>-T</c> script, following INCLUDE, searched in the
    /// adapted -L order). Null when the template has no linker script or no FLASH region.
    /// </summary>
    public FlashRegion? GetFlashRegion(RobotType type)
    {
        var commands = _config.Value.GetCommands(type.Name, "main");
        if (commands == null) return null;
        var ctx = new TemplateContext(Path.Combine(Path.GetTempPath(), "roboexp-nonexistent", "x.c"), "", _install.IncludeDirectory(type),
            _install.IncludeDirectory(type), CompilerDirectory);
        var adapter = new ToolchainAdapter(_install, _toolchain, type);
        foreach (var template in commands)
        {
            if (JMCompilerTemplate.ToStep(JMCompilerTemplate.Expand(template, ctx)) is not BuildStep.Run run) continue;
            var raw = JMCompilerTemplate.SplitCommandLine(run.CommandLine);
            var args = adapter.Adapt(raw, out _) ?? raw.Select(a => ToolchainAdapter.FixPathArgument(a.Replace('\\', '/'))).ToList();
            string? script = null;
            for (var i = 1; i < args.Count; i++)
            {
                if (args[i] == "-T" && i + 1 < args.Count) script = args[i + 1];
                else if (args[i].StartsWith("-T", StringComparison.Ordinal) && args[i].Length > 2) script = args[i][2..];
            }
            if (script == null) continue;
            var searchDirs = args.Where(a => a.StartsWith("-L", StringComparison.Ordinal) && a.Length > 2).Select(a => a[2..]).ToList();
            return LinkerScripts.FindFlashRegion(script, searchDirs);
        }
        return null;
    }
}

/// <summary>Minimal GNU ld script reader for MEMORY regions (comments stripped, INCLUDE followed).</summary>
public static partial class LinkerScripts
{
    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"\bINCLUDE\s+""?([^""\s;]+)""?")]
    private static partial Regex Include();

    [GeneratedRegex(@"^\s*(?<name>[A-Za-z_][\w]*)\s*(?:\([^)]*\))?\s*:\s*ORIGIN\s*=\s*(?<origin>0[xX][0-9A-Fa-f]+|\d+)\s*,\s*LENGTH\s*=\s*(?<len>0[xX][0-9A-Fa-f]+|\d+)\s*(?<unit>[KkMm])?", RegexOptions.Multiline)]
    private static partial Regex Region();

    public static FlashRegion? FindFlashRegion(string script, IReadOnlyList<string> searchDirs)
    {
        var path = Locate(script, searchDirs, null);
        return path == null ? null : Find(path, searchDirs, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Parses the text of a script for a FLASH region (without following INCLUDE).</summary>
    public static FlashRegion? ParseFlashRegion(string scriptText, string scriptPath)
    {
        foreach (Match m in Region().Matches(Comment().Replace(scriptText, "")))
        {
            if (!m.Groups["name"].Value.Equals("FLASH", StringComparison.OrdinalIgnoreCase)) continue;
            var length = ParseNumber(m.Groups["len"].Value) * (m.Groups["unit"].Value.ToUpperInvariant() switch { "K" => 1024UL, "M" => 1024UL * 1024, _ => 1UL });
            return new FlashRegion(ParseNumber(m.Groups["origin"].Value), length, scriptPath);
        }
        return null;
    }

    private static FlashRegion? Find(string path, IReadOnlyList<string> searchDirs, HashSet<string> seen)
    {
        if (!seen.Add(path)) return null;
        var text = File.ReadAllText(path, Gbk.Encoding);
        var region = ParseFlashRegion(text, path);
        if (region != null) return region;
        foreach (Match m in Include().Matches(Comment().Replace(text, "")))
        {
            var inc = Locate(m.Groups[1].Value, searchDirs, Path.GetDirectoryName(path));
            if (inc != null && Find(inc, searchDirs, seen) is { } r) return r;
        }
        return null;
    }

    private static string? Locate(string name, IReadOnlyList<string> searchDirs, string? currentDir)
    {
        if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
        foreach (var dir in searchDirs.Append(currentDir))
        {
            if (dir == null) continue;
            var p = VendorInstall.FindChildIgnoreCase(dir, name);
            if (p != null && File.Exists(p)) return p;
        }
        return null;
    }

    private static ulong ParseNumber(string s) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ulong.Parse(s[2..], NumberStyles.HexNumber) : ulong.Parse(s);
}
