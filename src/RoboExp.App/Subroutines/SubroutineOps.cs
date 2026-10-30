using RoboExp.Core.Library;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;
using RoboExp.Core.Rcu;

namespace RoboExp.App.Subroutines;

/// <summary>A private subroutine shown by the manager: its entry and position in <see cref="ProjectData.UsedProjectInfoList"/>.</summary>
public sealed record PrivateSub(int ListIndex, ProjectInfo Info);

/// <summary>A used subroutine whose library copy differs (BaseView.GetNeedUpdateSubList).</summary>
public sealed record SubUpdate(ProjectInfo Library, ProjectInfo Project);

/// <summary>Model logic of SubManagerDlg / UpdateSubDlg / BaseView.UpdatePrivateModuleFromLib, free of UI.</summary>
public static class SubroutineOps
{
    public const string ProjectFileExtension = ".rcu";

    /// <summary>SubManagerDlg.RefreshTreeView: the non-system entries of the used-function list, in list order.</summary>
    public static IReadOnlyList<PrivateSub> PrivateSubs(ProjectData data, Func<string, bool> isSystemFunction)
    {
        var list = data.UsedProjectInfoList ?? [];
        var result = new List<PrivateSub>();
        for (var i = 0; i < list.Count; i++)
        {
            if (!isSystemFunction(list[i].ProjectName ?? "")) result.Add(new PrivateSub(i, list[i]));
        }
        return result;
    }

    /// <summary>
    /// SubManagerDlg button1/button2: moves the private sub at <paramref name="position"/> (index into
    /// <paramref name="subs"/>) past its visible neighbour (<paramref name="delta"/> −1 = up, +1 = down); hidden system
    /// entries in between keep their places relative to each other. Returns false at either end.
    /// </summary>
    public static bool Move(ProjectData data, IReadOnlyList<PrivateSub> subs, int position, int delta)
    {
        var target = position + delta;
        if (data.UsedProjectInfoList is not { } list || position < 0 || position >= subs.Count || target < 0 || target >= subs.Count)
            return false;
        var from = subs[position].ListIndex;
        var value = list[from];
        list.RemoveAt(from);
        list.Insert(subs[target].ListIndex, value);
        return true;
    }

    /// <summary>ProjectData.RemoveUserdProjectInfo: removes the first entry of that (case-sensitive) name.</summary>
    public static bool Remove(ProjectData data, string projectName)
    {
        if (data.UsedProjectInfoList is not { } list) return false;
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].ProjectName, projectName, StringComparison.Ordinal))
            {
                list.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>The flowchart's graphic objects (empty for code projects).</summary>
    public static IEnumerable<GraphicObject> DrawingObjects(RcuFile file) =>
        file.FlowData?.DesignSurfaceData?.DrawingObjects?.List ?? (IEnumerable<GraphicObject>)[];

    /// <summary>FlowView.CheckSubHasUsed: a Function icon of the flowchart calls <paramref name="funcName"/>.</summary>
    public static bool IsUsedInFlow(RcuFile file, string funcName) =>
        DrawingObjects(file).Any(g => g.DataObject is FuncData f && f.ProjectName == funcName);

    /// <summary>File name of a stored path; vendor files hold Windows paths, so both separators count.</summary>
    public static string FileNameOf(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];

    /// <summary>
    /// SubManagerDlg: the sub's file is the project directory + the file name of its stored ProjectPath
    /// (falls back to <c>&lt;ProjectName&gt;.rcu</c> when no path is stored).
    /// </summary>
    public static string SubRcuPath(string projectRcuPath, ProjectInfo info)
    {
        var name = FileNameOf(info.ProjectPath ?? "");
        if (name.Length == 0) name = info.ProjectName + ProjectFileExtension;
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectRcuPath))!, name);
    }

    /// <summary>
    /// SubManagerDlg.listView1_AfterLabelEdit: sets the caption of the used entry and of the sub's own .rcu.
    /// The original replaced the sub file's whole ProjectInfo with the parent's entry; the port changes only the caption
    /// so a sub edited since it was referenced keeps its signature.
    /// </summary>
    public static void RenameCaption(ProjectInfo info, string caption, string projectRcuPath)
    {
        info.ProjectCaption = caption;
        var path = SubRcuPath(projectRcuPath, info);
        if (!File.Exists(path)) return;
        RcuFile file;
        try { file = RcuReader.Read(path); }
        catch (InvalidDataException) { return; }
        if (file.ProjectData?.ProjectInfo is not { } own) return;
        own.ProjectCaption = caption;
        RcuWriter.Write(path, file);
    }

    /// <summary>BaseView.GetNeedUpdateSubList (non-system entries only, as UpdatePrivateModuleFromLib uses it).</summary>
    public static IReadOnlyList<SubUpdate> NeedUpdate(ProjectData data, Func<string, ProjectInfo?> libraryLookup, Func<string, bool> isSystemFunction)
    {
        var result = new List<SubUpdate>();
        foreach (var used in data.UsedProjectInfoList ?? [])
        {
            var name = used.ProjectName ?? "";
            if (isSystemFunction(name)) continue;
            if (libraryLookup(name) is { } lib && !lib.CheckIsEquals(used)) result.Add(new SubUpdate(lib, used));
        }
        return result;
    }

    /// <summary>
    /// PublicMethod.CopyProjectFiles + SaveSubProjects: copies <c>&lt;name&gt;.rcu</c> and <c>&lt;name&gt;.c</c> and,
    /// recursively, those of the non-system subs it uses from <paramref name="sourceDir"/> to <paramref name="targetDir"/>.
    /// Returns the missing file name (original "SrcNotComplete") or null on success. Same directories: nothing to do.
    /// </summary>
    public static string? CopySubFiles(string projectName, string sourceDir, string targetDir, Func<string, bool> isSystemFunction)
    {
        if (string.Equals(Path.GetFullPath(sourceDir).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return null;
        Directory.CreateDirectory(targetDir);
        var copied = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        pending.Enqueue(projectName);
        copied.Add(projectName);
        while (pending.Count > 0)
        {
            var name = pending.Dequeue();
            var rcu = Path.Combine(sourceDir, name + ProjectFileExtension);
            var c = Path.Combine(sourceDir, name + ".c");
            if (!File.Exists(rcu)) return name + ProjectFileExtension;
            if (!File.Exists(c)) return name + ".c";
            File.Copy(rcu, Path.Combine(targetDir, name + ProjectFileExtension), overwrite: true);
            File.Copy(c, Path.Combine(targetDir, name + ".c"), overwrite: true);
            ProjectData? data;
            try { data = RcuReader.Read(rcu).ProjectData; }
            catch (InvalidDataException) { data = null; }
            foreach (var used in data?.UsedProjectInfoList ?? [])
            {
                var sub = used.ProjectName ?? "";
                if (sub.Length > 0 && !isSystemFunction(sub) && copied.Add(sub)) pending.Enqueue(sub);
            }
        }
        return null;
    }

    /// <summary>
    /// UpdatePrivateModuleFromLib for one entry: copies the library sub into the project directory, replaces the used
    /// entry by the library signature and adapts the calling Function icons. Returns the missing file name on failure.
    /// </summary>
    public static string? ApplyUpdate(RcuFile project, string projectRcuPath, SubUpdate update, Func<string, bool> isSystemFunction)
    {
        var data = project.ProjectData;
        var list = data?.UsedProjectInfoList;
        var index = list?.IndexOf(update.Project) ?? -1;
        if (list == null || index < 0) return null;
        var name = update.Library.ProjectName ?? "";
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectRcuPath))!;
        var libDir = Path.GetDirectoryName(update.Library.ProjectPath ?? "");
        if (string.IsNullOrEmpty(libDir)) return name + ProjectFileExtension;
        if (CopySubFiles(name, libDir, projectDir, isSystemFunction) is { } missing) return missing;
        var copy = Path.Combine(projectDir, name + ProjectFileExtension);
        var replacement = (File.Exists(copy) ? UserLibrary.LoadFuncProjectInfo(copy, out _) : null) ?? update.Library;
        list[index] = replacement;
        UpdateUsedModuleInfoInFlow(project, update.Project, replacement);
        return null;
    }

    /// <summary>
    /// FlowView.UpdateUsedModuleInfoInFlow: for Function icons calling the sub, clears the return variable when the
    /// return type changed, rebuilds the argument list from the call text when the parameters changed (missing
    /// arguments take the parameter default), and takes the new icon image.
    /// </summary>
    public static void UpdateUsedModuleInfoInFlow(RcuFile file, ProjectInfo oldInfo, ProjectInfo newInfo)
    {
        var returnEquals = string.Equals(oldInfo.ReturnType?.Type, newInfo.ReturnType?.Type, StringComparison.Ordinal);
        var paramsEqual = oldInfo.CheckParamListEquals(newInfo);
        foreach (var g in DrawingObjects(file))
        {
            if (g.DataObject is not FuncData f || !string.Equals(f.ProjectName, oldInfo.ProjectName, StringComparison.Ordinal)) continue;
            if (!returnEquals) f.ReturnName = "";
            if (!paramsEqual)
            {
                var args = CallArguments(f.Code ?? "");
                f.ValueList ??= new();
                f.ValueList.Clear();
                var i = 0;
                foreach (var p in newInfo.ParamList ?? [])
                {
                    f.ValueList.Add(i < args.Count ? args[i] : p.DefaultValue);
                    i++;
                }
            }
            if (g.DrawObject is IconDraw icon) icon.Image = newInfo.ProjectIcon;
        }
    }

    /// <summary>The comma-separated, trimmed text between the first '(' and the first ')' of a call (both after index 0).</summary>
    public static IReadOnlyList<string> CallArguments(string code)
    {
        var open = code.IndexOf('(');
        var close = code.IndexOf(')');
        var inner = open > 0 && close > 0 && close > open ? code.Substring(open + 1, close - open - 1) : "";
        return inner.Length == 0 ? [] : inner.Split(',').Select(a => a.Trim()).ToList();
    }
}
