using System.Text;
using System.Xml;

namespace RoboExp.Core.Build;

/// <summary>Compiler/JMCompiler.xml: per software type, <c>&lt;main&gt;</c>/<c>&lt;func&gt;</c> groups of <c>cmd0..cmdN</c> templates.</summary>
public sealed class JMCompilerConfig
{
    private readonly XmlDocument _doc;

    private JMCompilerConfig(XmlDocument doc) => _doc = doc;

    public static JMCompilerConfig Load(string path)
    {
        var doc = new XmlDocument();
        doc.Load(path);
        if (doc.DocumentElement == null) throw new InvalidDataException($"Read root node failed! In file {path}.");
        return new JMCompilerConfig(doc);
    }

    public static string GroupName(BuildGroup group) => group == BuildGroup.Main ? "main" : "func";

    /// <summary>
    /// The command templates of <c>&lt;softType&gt;&lt;group&gt;</c> (JMcompiler.GetCmdStrings: cmd0, cmd1, ... up to the first gap),
    /// or null when the type or group is absent.
    /// </summary>
    public IReadOnlyList<string>? GetCommands(string softType, string group)
    {
        var groupNode = _doc.DocumentElement!.SelectSingleNode(softType)?.SelectSingleNode(group);
        if (groupNode == null) return null;
        var list = new List<string>();
        for (var i = 0; i < groupNode.ChildNodes.Count; i++)
        {
            var cmd = groupNode.SelectSingleNode("cmd" + i);
            if (cmd == null) break;
            list.Add(cmd.InnerText);
        }
        return list;
    }
}

/// <summary>Values JMCompiler.exe receives on its command line (MainForm.Compile).</summary>
/// <param name="SourcePath">?F: the project's .c file.</param>
/// <param name="WorkDir">?W and the process working directory; also where "?O" collects *.o.</param>
/// <param name="IncludeDirs">'*'-separated include directories (?I).</param>
/// <param name="LibDirs">'*'-separated library directories (?L).</param>
/// <param name="CompilerDir">?C: directory of JMCompiler.exe (install/Compiler).</param>
public sealed record TemplateContext(string SourcePath, string WorkDir, string IncludeDirs, string LibDirs, string CompilerDir);

public abstract record BuildStep
{
    /// <summary>Run a tool; <see cref="CommandLine"/> is the expanded Windows-style command line.</summary>
    public sealed record Run(string CommandLine) : BuildStep;

    /// <summary><c>?D file</c>: delete the file if present.</summary>
    public sealed record Delete(string Path) : BuildStep;

    /// <summary><c>?T file</c>: stop executing further commands when the file does not exist.</summary>
    public sealed record Test(string Path) : BuildStep;
}

/// <summary>Placeholder semantics of JMCompiler (JMcompiler.Main/GetRealPath/GetIncDir/GetLibDir), byte-for-byte.</summary>
public static class JMCompilerTemplate
{
    /// <summary>GetIncDir: <c>-I"dir"</c> for every non-empty '*'-separated entry.</summary>
    public static string IncludeArgument(string dirs) => DirArgument(dirs, "-I");

    /// <summary>GetLibDir: <c>-L"dir"</c> for every non-empty '*'-separated entry.</summary>
    public static string LibArgument(string dirs) => DirArgument(dirs, "-L");

    private static string DirArgument(string dirs, string flag)
    {
        if (string.IsNullOrEmpty(dirs) || dirs.Equals("default", StringComparison.OrdinalIgnoreCase)) return "";
        var sb = new StringBuilder();
        foreach (var part in dirs.Replace("\"", "").Trim().Split('*'))
        {
            var d = part.Trim();
            if (d.Length > 0) sb.Append(' ').Append(flag).Append('"').Append(d).Append('"');
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Expands one command template: ?E ?N ?P ?F (in that order), then ?W ?I ?L ?C, then <c>"?O"</c>
    /// (quoted list of the *.o files currently in the work directory, each followed by a space).
    /// </summary>
    public static string Expand(string template, TemplateContext ctx)
    {
        var text = template.Trim();
        var source = ctx.SourcePath;
        var dir = (Path.GetDirectoryName(source) ?? "").TrimEnd('\\', '/');
        text = text.Replace("?E", Path.GetExtension(source))
            .Replace("?N", Path.GetFileNameWithoutExtension(source))
            .Replace("?P", dir)
            .Replace("?F", source);

        var workDir = NormalizeWorkDir(ctx.WorkDir, source);
        text = text.Replace("?W", workDir)
            .Replace("?I", IncludeArgument(ctx.IncludeDirs))
            .Replace("?L", LibArgument(ctx.LibDirs))
            .Replace("?C", ctx.CompilerDir.TrimEnd('\\', '/'));

        if (text.Contains("\"?O\"", StringComparison.Ordinal))
        {
            var objects = new StringBuilder();
            if (workDir.Length > 0)
            {
                foreach (var o in Directory.GetFiles(workDir, "*.o", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
                    objects.Append('"').Append(o).Append("\" ");
            }
            text = text.Replace("\"?O\"", objects.ToString());
        }
        return text;
    }

    /// <summary>JMCompiler's work directory rules: "default"/empty = source directory; trailing separators trimmed; missing = "".</summary>
    public static string NormalizeWorkDir(string workDir, string sourcePath)
    {
        var w = (workDir ?? "").Replace("\"", "");
        if (w.Length == 0 || w.Equals("default", StringComparison.OrdinalIgnoreCase)) w = Path.GetDirectoryName(sourcePath) ?? "";
        w = w.TrimEnd('\\', '/');
        return w.Length > 0 && !Directory.Exists(w) ? "" : w;
    }

    /// <summary>Classifies an expanded command (<c>?D</c>/<c>?T</c> prefix, quotes removed, else a tool invocation).</summary>
    public static BuildStep ToStep(string expanded)
    {
        if (expanded.StartsWith("?D", StringComparison.Ordinal))
            return new BuildStep.Delete(expanded.Replace("?D", "").Replace("\"", "").Trim());
        if (expanded.StartsWith("?T", StringComparison.Ordinal))
            return new BuildStep.Test(expanded.Replace("?T", "").Replace("\"", "").Trim());
        return new BuildStep.Run(expanded);
    }

    /// <summary>
    /// Splits a command line the way CreateProcess/CommandLineToArgvW does for these templates: whitespace separates
    /// arguments outside quotes, quotes toggle quoting and are removed (so <c>-Wl,-Map="a b",-cref</c> is one argument).
    /// Backslashes are literal (template paths never escape quotes).
    /// </summary>
    public static IReadOnlyList<string> SplitCommandLine(string commandLine)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        foreach (var c in commandLine)
        {
            if (c == '"') { inQuotes = !inQuotes; hasToken = true; continue; }
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (hasToken) { args.Add(current.ToString()); current.Clear(); hasToken = false; }
                continue;
            }
            current.Append(c);
            hasToken = true;
        }
        if (hasToken) args.Add(current.ToString());
        return args;
    }
}
