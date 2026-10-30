using RoboExp.Core.Install;
using RoboExp.Core.Model;

namespace RoboExp.App.NewProject;

/// <summary>Pure logic of the original FileNewFrm (NewFileName, btn_Ok_Click validation, GetProjectInfo path).</summary>
public static class NewProjectRules
{
    public const string Extension = ".rcu";

    /// <summary>Tree node (0 Flow / 1 Code) × list item (0 Application / 1 Subroutine).</summary>
    public static ProjectType KindOf(bool code, bool subroutine) => (code, subroutine) switch
    {
        (false, false) => ProjectType.FlowApp,
        (false, true) => ProjectType.FlowFunc,
        (true, false) => ProjectType.CodeApp,
        _ => ProjectType.CodeFunc,
    };

    public static bool IsSubroutine(ProjectType t) => t is ProjectType.FlowFunc or ProjectType.CodeFunc;

    /// <summary>
    /// Port of FilePathInfo.MyProjectsDirectory (originally &lt;install&gt;/Robots/&lt;TYPE&gt;/MyProjects, inside the read-only
    /// vendor tree): ~/Documents/RoboExp/&lt;TYPE&gt;/MyProjects.
    /// </summary>
    public static string DefaultProjectsDirectory(RobotType type) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RoboExp", type.Name, "MyProjects");

    /// <summary>NewFileName: "JM" + first 3 chars of the item tag ("App"/"Sub") + n; unique by folder, or by .rcu file inside a project dir.</summary>
    public static string DefaultName(string directory, bool subroutine, bool insideProject)
    {
        var tag = subroutine ? "Sub" : "App";
        for (var n = 1; ; n++)
        {
            var name = "JM" + tag + n;
            var exists = insideProject ? File.Exists(Path.Combine(directory, name + Extension)) : Directory.Exists(Path.Combine(directory, name));
            if (!exists) return name;
        }
    }

    /// <summary>GetProjectInfo path: &lt;dir&gt;/&lt;name&gt;/&lt;name&gt;.rcu, or &lt;dir&gt;/&lt;name&gt;.rcu inside a project dir.</summary>
    public static string TargetPath(string directory, string name, bool insideProject) =>
        Path.GetFullPath(insideProject ? Path.Combine(directory, name + Extension) : Path.Combine(directory, name, name + Extension));

    public enum Error { None, Exists, Blank, SystemName, InvalidChar, FirstCharInvalid, BadPath }

    /// <summary>XWBRM message per error; the "exists" message depends on the selected item like the original.</summary>
    public static string MessageKey(Error e, bool subroutine) => e switch
    {
        Error.Exists => subroutine ? "Xwb:FileNewFrm_ErrSameFile" : "Xwb:FileNewFrm_ErrAppl",
        Error.Blank => "Xwb:Existedblank",
        Error.SystemName => "Xwb:IsSystemSubName",
        Error.InvalidChar => "Xwb:Invalidchar",
        Error.FirstCharInvalid => "Xwb:Firstcharisinvalid",
        Error.BadPath => "Port:New.BadPath",
        _ => "",
    };

    /// <summary>
    /// btn_Ok_Click. The original CheckPath ("X:\" drive form) becomes: the location must be an absolute path.
    /// Subroutine names must be C identifiers (StringCheck); an empty name counts as invalid (ExistedFirstInvalidChar).
    /// </summary>
    public static Error Validate(string directory, string name, bool subroutine, bool insideProject, Func<string, bool> isSystemFunction)
    {
        directory = directory.Trim();
        name = name.Trim();
        if (directory.Length == 0 || !Path.IsPathFullyQualified(directory)) return Error.BadPath;
        if (insideProject ? File.Exists(Path.Combine(directory, name + Extension)) : Directory.Exists(Path.Combine(directory, name)))
            return Error.Exists;
        if (name.Contains(' ')) return Error.Blank;
        if (isSystemFunction(name)) return Error.SystemName;
        if (subroutine)
        {
            if (name.Any(c => !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))) return Error.InvalidChar;
            if (name.Length < 1 || !(char.IsLetter(name[0]) || name[0] == '_')) return Error.FirstCharInvalid;
        }
        else if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
        {
            return Error.InvalidChar; // the original relied on Windows rejecting the path; the port checks up front
        }
        return Error.None;
    }
}
