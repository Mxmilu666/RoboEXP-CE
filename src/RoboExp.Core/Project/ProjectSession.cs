using RoboExp.Core.Build;
using RoboExp.Core.CodeGen;
using RoboExp.Core.Flow;
using RoboExp.Core.Install;
using RoboExp.Core.Library;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;
using RoboExp.Core.Rcu;

namespace RoboExp.Core.Project;

/// <summary>A compiler diagnostic plus the flowchart object its line maps to (FlowView.SelectLine), if any.</summary>
public sealed record MappedDiagnostic(CompilerDiagnostic Diagnostic, GraphicObject? Object);

/// <summary>
/// Outcome of <see cref="ProjectSession.CompileAsync"/>. <see cref="Build"/> is null when a pre-check error stopped
/// compilation (MainForm.Compile does not save or run JMCompiler then).
/// </summary>
public sealed record CompileOutcome(PreCompileReport Checks, BuildResult? Build, IReadOnlyList<MappedDiagnostic> Diagnostics)
{
    public bool Success => !Checks.HasError && Build is { Success: true };
}

/// <summary>
/// An open project (BaseView/FlowView/FrmCodeApp/FrmCodeFunc minus UI): open, regenerate, save (.rcu + &lt;name&gt;.c in GBK +
/// HardwareInfo.c), create new projects (MainForm.NewFile/FileNewFrm), compile via <see cref="Builder"/>, and manage user
/// subroutine projects (copied next to the project, included as <c>#include "X.c"</c>).
/// </summary>
public sealed class ProjectSession
{
    private readonly VendorInstall _install;
    private SystemLibrary? _library;
    private RcuInfo? _rcuInfo;

    private ProjectSession(RcuFile file, string rcuPath, VendorInstall install, RobotType type)
    {
        File = file;
        RcuPath = Path.GetFullPath(rcuPath);
        _install = install;
        Type = type;
        Graph = file.FlowData != null ? new FlowGraph(file.FlowData.DesignSurfaceData?.DrawingObjects) : null;
    }

    public RcuFile File { get; private set; }

    /// <summary>Full path of the .rcu; the C file is <c>&lt;dir&gt;/&lt;file name&gt;.c</c>.</summary>
    public string RcuPath { get; private set; }

    /// <summary>The vendor install this session reads libraries/resources from.</summary>
    public VendorInstall Install => _install;

    /// <summary>The controller codegen targets (the original's global SoftwareType, not the stored one).</summary>
    public RobotType Type { get; }

    /// <summary>Flow semantics for flow projects; null for CodeApp/CodeFunc.</summary>
    public FlowGraph? Graph { get; private set; }

    public ProjectData Data => File.ProjectData ?? throw new InvalidDataException("RCU file without ProjectData");
    public ProjectInfo Info => Data.ProjectInfo ?? throw new InvalidDataException("RCU file without ProjectInfo");
    public GraphicObjectCollection? DrawingObjects => File.FlowData?.DesignSurfaceData?.DrawingObjects;
    public bool IsFlow => Info.ProjectType is ProjectType.FlowApp or ProjectType.FlowFunc;
    public bool IsApp => Info.ProjectType is ProjectType.FlowApp or ProjectType.CodeApp;
    public string ProjectDirectory => Path.GetDirectoryName(RcuPath)!;
    public string CPath => Path.Combine(ProjectDirectory, Path.GetFileNameWithoutExtension(RcuPath) + ".c");
    public string HardwareInfoPath => Path.Combine(ProjectDirectory, "HardwareInfo.c");

    /// <summary>System library of <see cref="Type"/> (IsSystemFunction = Include.xml names; Functions = JMlib.xml).</summary>
    public SystemLibrary Library => _library ??= SystemLibrary.Load(_install, Type);

    public RcuInfo RcuInfo => _rcuInfo ??= RcuInfo.Load(_install, Type);

    public CodeGenOptions CodeGenOptions => new(Type.SoftwareType, OuterType.Entity, Library.IsSystemFunction);

    /// <summary>Last generation (flow projects), refreshed by <see cref="Regenerate"/>.</summary>
    public CodeGenResult? LastGeneration { get; private set; }

    /// <summary>
    /// Opens an .rcu (PublicMethod.DeSerializeObjectFromFile sets ProjectPath to the opened path). The robot type defaults to
    /// the stored SoftwareType.
    /// </summary>
    public static ProjectSession Open(string rcuPath, VendorInstall install, RobotType? type = null)
    {
        var file = RcuReader.Open(rcuPath);
        return new ProjectSession(file, rcuPath, install, type ?? TypeOf(file));
    }

    /// <summary>Wraps an in-memory file (tests, snapshots). Nothing is written until <see cref="Save"/>.</summary>
    public static ProjectSession Create(RcuFile file, string rcuPath, VendorInstall install, RobotType? type = null) =>
        new(file, rcuPath, install, type ?? TypeOf(file));

    private static RobotType TypeOf(RcuFile file)
    {
        var st = file.ProjectData?.ProjectInfo?.SoftwareType ?? SoftwareType.Default;
        return RobotType.Supported.FirstOrDefault(t => t.SoftwareType == st)
               ?? throw new NotSupportedException($"Project robot type {st} is not supported; pass a type explicitly");
    }

    /// <summary>Adopts <paramref name="snapshot"/> (e.g. an undo state) as the live file; Graph is rebuilt over its objects.</summary>
    public void Restore(RcuFile snapshot)
    {
        File = snapshot;
        Graph = snapshot.FlowData != null ? new FlowGraph(snapshot.FlowData.DesignSurfaceData?.DrawingObjects) : null;
        LastGeneration = null;
        if (snapshot.ProjectData?.ProjectInfo is { } info) info.ProjectPath = RcuPath;
    }

    /// <summary>Serializes the live file (BinaryFormatter-compatible) without touching disk.</summary>
    public void WriteRcu(Stream stream) => RcuWriter.Write(stream, File);

    /// <summary>
    /// FlowView.GetCodeText for flow projects (updates AllCode, Codes, BeginLine/EndLine); for code projects the text
    /// their SaveCode writes. Returns the &lt;name&gt;.c content.
    /// </summary>
    public string Regenerate()
    {
        if (Graph != null && IsFlow)
        {
            LastGeneration = CodeGenerator.RegenerateAllCode(Data, Graph, CodeGenOptions);
            return LastGeneration.Text;
        }
        return CodeSourceText(Data);
    }

    /// <summary>FrmCodeApp/FrmCodeFunc.SaveCode text: CodeApp = Code; CodeFunc = Global CRLF CRLF declaration CRLF Code CRLF.</summary>
    public static string CodeSourceText(ProjectData data)
    {
        var f = data.CodeFrameData ?? new CodeFrameData();
        return data.ProjectInfo?.ProjectType switch
        {
            ProjectType.CodeApp => f.Code ?? "",
            ProjectType.CodeFunc => f.Global + "\r\n\r\n" + data.ProjectInfo.GetFuncDeclareCodeLine() + "\r\n" + f.Code + "\r\n",
            _ => f.AllCode ?? "",
        };
    }

    /// <summary>
    /// BaseView.Save: UpdateProjectDataBeforeSave (hardware usage), .rcu, &lt;name&gt;.c (GBK, text verbatim, CRLF from
    /// generation) and HardwareInfo.c. The .rcu is written after regeneration, so its AllCode equals the .c.
    /// </summary>
    public void Save()
    {
        Info.ProjectPath = RcuPath;
        Directory.CreateDirectory(ProjectDirectory);
        var source = Regenerate();
        if (IsFlow && LastGeneration != null) PreCompileChecks.UpdateUsedHardwareInstance(Data, LastGeneration.UsedFunctionIcons);
        RcuWriter.Write(RcuPath, File);
        HardwareInfoFile.WriteTextFile(CPath, source);
        HardwareInfoFile.WriteTextFile(HardwareInfoPath, HardwareInfoFile.Create(Data, Type.SoftwareType, RcuInfo));
    }

    /// <summary>
    /// SaveAs: renames the project after the new file (ProjectName/Caption; CodeFunc guards follow the rename), saves, and copies
    /// the user subroutine projects it uses from the old directory (PublicMethod.SaveSubProjects).
    /// </summary>
    public void SaveAs(string rcuPath)
    {
        var full = Path.GetFullPath(rcuPath);
        if (string.Equals(full, RcuPath, StringComparison.OrdinalIgnoreCase))
        {
            Save();
            return;
        }
        var oldDir = ProjectDirectory;
        var oldName = Info.ProjectName ?? "";
        var newName = Path.GetFileNameWithoutExtension(full);
        Info.ProjectName = newName;
        Info.ProjectCaption = newName;
        if (Info.ProjectType == ProjectType.CodeFunc && Data.CodeFrameData is { } cf)
        {
            cf.Global = (cf.Global ?? "")
                .Replace("#ifndef _" + oldName.ToUpperInvariant() + "_", "#ifndef _" + newName.ToUpperInvariant() + "_")
                .Replace("#define _" + oldName.ToUpperInvariant() + "_", "#define _" + newName.ToUpperInvariant() + "_");
        }
        RcuPath = full;
        Save();
        SaveSubProjects(Data, oldDir, ProjectDirectory, [newName]);
    }

    /// <summary>
    /// PublicMethod.SaveSubProjects: copy every non-system used project (.rcu + .c) from <paramref name="srcDir"/> to
    /// <paramref name="newDir"/>, recursively through their own used projects. No-op when the directories are the same.
    /// </summary>
    public void SaveSubProjects(ProjectData data, string srcDir, string newDir, List<string> copied)
    {
        Directory.CreateDirectory(newDir);
        if (string.Equals(Path.GetFullPath(srcDir).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(newDir).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)) return;
        foreach (var used in ((IEnumerable<ProjectInfo>?)data.UsedProjectInfoList ?? []).ToList())
        {
            var name = used.ProjectName ?? "";
            if (Library.IsSystemFunction(name) || copied.Contains(name, StringComparer.Ordinal)) continue;
            CopyProjectFiles(name, srcDir, newDir);
            copied.Add(name);
            var sub = RcuReader.Read(Path.Combine(srcDir, name + ".rcu")).ProjectData;
            if (sub != null) SaveSubProjects(sub, srcDir, newDir, copied);
        }
    }

    /// <summary>PublicMethod.CopyProjectFiles: &lt;name&gt;.rcu and &lt;name&gt;.c must both exist.</summary>
    public static void CopyProjectFiles(string projectName, string srcDir, string newDir)
    {
        var rcu = Path.Combine(srcDir, projectName + ".rcu");
        var c = Path.Combine(srcDir, projectName + ".c");
        if (!System.IO.File.Exists(rcu)) throw new FileNotFoundException("Source not complete", rcu);
        if (Path.GetFullPath(srcDir) != Path.GetFullPath(newDir)) System.IO.File.Copy(rcu, Path.Combine(newDir, projectName + ".rcu"), true);
        if (!System.IO.File.Exists(c)) throw new FileNotFoundException("Source not complete", c);
        if (Path.GetFullPath(srcDir) != Path.GetFullPath(newDir)) System.IO.File.Copy(c, Path.Combine(newDir, projectName + ".c"), true);
    }

    /// <summary>
    /// ProjectData.AddUsedProjectInfo: returns the used signature named <paramref name="projectName"/>, adding a clone of
    /// <paramref name="source"/> (a library function or a user subroutine's ProjectInfo) when absent. User subroutines
    /// (non-system, other than this project, with a path) get their .rcu/.c and their own subroutines copied here.
    /// </summary>
    public ProjectInfo? AddUsedProjectInfo(string projectName, ProjectInfo? source)
    {
        if (Data.GetUsedProjectInfo(projectName) is { } existing) return existing;
        if (source == null) return null;
        var info = CloneInfo(source);
        CopyUserSubroutine(info);
        (Data.UsedProjectInfoList ??= new()).Add(info);
        return info;
    }

    /// <summary>
    /// ProjectData.RefProjectInfo: replaces every used signature named like <paramref name="info"/> by it (after copying a user
    /// subroutine's files here). The original asks before overwriting an existing file; callers confirm beforehand.
    /// </summary>
    public void RefProjectInfo(ProjectInfo info)
    {
        var list = Data.UsedProjectInfoList ??= new();
        for (var i = list.Count - 1; i >= 0; i--)
            if (string.Equals(list[i].ProjectName, info.ProjectName, StringComparison.Ordinal)) list.RemoveAt(i);
        CopyUserSubroutine(info);
        list.Add(info);
    }

    private void CopyUserSubroutine(ProjectInfo info)
    {
        var name = info.ProjectName ?? "";
        if (Library.IsSystemFunction(name) || string.Equals(Info.ProjectName, name, StringComparison.Ordinal)
            || string.IsNullOrEmpty(info.ProjectPath)) return;
        var srcDir = Path.GetDirectoryName(info.ProjectPath)!;
        if (string.Equals(Path.GetFullPath(srcDir), ProjectDirectory, StringComparison.OrdinalIgnoreCase)) return;
        CopyProjectFiles(name, srcDir, ProjectDirectory);
        var sub = RcuReader.Read(info.ProjectPath).ProjectData;
        if (sub != null) SaveSubProjects(sub, srcDir, ProjectDirectory, [name]);
    }

    private static ProjectInfo CloneInfo(ProjectInfo source)
    {
        var file = new RcuFile { ProjectType = ProjectType.CodeFunc, CodeData = new CodeData { ProjectData = new ProjectData { ProjectInfo = source } } };
        var ms = new MemoryStream();
        RcuWriter.Write(ms, file);
        ms.Position = 0;
        return RcuReader.Read(ms).ProjectData!.ProjectInfo!;
    }

    /// <summary>MainForm.Compile pre-checks (flow projects): param/return checks and, for X3/X4, hardware instances.</summary>
    public PreCompileReport RunPreCompileChecks()
    {
        if (!IsFlow || Graph == null) return new PreCompileReport([]);
        Regenerate();
        var display = ProjectDirectory + "\\" + Info.ProjectName + ".c";
        return PreCompileChecks.Run(Data, Graph, LastGeneration!.UsedFunctionIcons, Type.SoftwareType, display);
    }

    /// <summary>
    /// MainForm.Compile: pre-checks (stop on error), Save(), build with group main (apps) / func (subroutines), and map
    /// diagnostics in &lt;name&gt;.c to the object whose BeginLine matches.
    /// </summary>
    public async Task<CompileOutcome> CompileAsync(Builder builder, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var checks = RunPreCompileChecks();
        if (checks.HasError) return new CompileOutcome(checks, null, []);
        Save();
        var result = await builder.BuildAsync(CPath, Type, IsApp ? BuildGroup.Main : BuildGroup.Func, log, ct).ConfigureAwait(false);
        Regenerate(); // Save regenerated already; keep line state current for mapping
        var mapped = result.Diagnostics.Select(d => new MappedDiagnostic(d, MapToObject(d))).ToList();
        return new CompileOutcome(checks, result, mapped);
    }

    /// <summary>The flowchart object for a diagnostic located in this project's .c (DockOutputBox double-click → SelectLine).</summary>
    public GraphicObject? MapToObject(CompilerDiagnostic d)
    {
        if (Graph == null || d.Line <= 0 || d.File.Length == 0) return null;
        string full;
        try { full = Path.GetFullPath(d.File.Replace('\\', Path.DirectorySeparatorChar), ProjectDirectory); }
        catch (ArgumentException) { return null; }
        return string.Equals(full, CPath, StringComparison.OrdinalIgnoreCase) ? Graph.FindObjectAtLine(d.Line) : null;
    }

    /// <summary>
    /// MainForm.NewFile + FileNewFrm.GetProjectInfo: builds the ProjectInfo (ReturnType "long" for apps, "void" for
    /// subroutines), the system hardware items that have instances, a Start icon at (IconWidth*2, IconHeight*2) centred for
    /// flow projects, the code templates for code projects, then saves .rcu/.c/HardwareInfo.c.
    /// </summary>
    /// <param name="rcuPath">FileNewFrm uses <c>&lt;dir&gt;/&lt;name&gt;/&lt;name&gt;.rcu</c> (or <c>&lt;dir&gt;/&lt;name&gt;.rcu</c> inside an existing project dir).</param>
    public static ProjectSession CreateNew(string rcuPath, ProjectType kind, RobotType type, VendorInstall install,
        UiLanguage lang = UiLanguage.Default, EncodedImage? projectIcon = null, EncodedImage? startIcon = null, string description = "")
    {
        var full = Path.GetFullPath(rcuPath);
        var name = Path.GetFileNameWithoutExtension(full);
        var info = new ProjectInfo
        {
            ProjectIcon = projectIcon,
            ProjectPath = full,
            ProjectCaption = name,
            ProjectDescription = description,
            ProjectName = name,
            SoftwareType = type.SoftwareType,
            OuterType = OuterType.Entity,
            ProjectType = kind,
        };
        info.ReturnType!.Type = kind is ProjectType.FlowApp or ProjectType.CodeApp ? "long" : "void";
        var data = new ProjectData { ProjectInfo = info };
        data.HardwareTable!.SoftwareType = type.SoftwareType;
        if (LibraryProjectInfo.LoadSystemHardwareTable(install, type, lang) is { } system)
            foreach (var item in system.HardwareItemList!)
                if (item.InstanceList is { Count: > 0 }) data.HardwareTable.HardwareItemList!.Add(item);

        var file = new RcuFile { ProjectType = kind };
        switch (kind)
        {
            case ProjectType.FlowApp or ProjectType.FlowFunc:
                var objects = new GraphicObjectCollection();
                objects.List!.Add(NewStartIcon(startIcon));
                file.FlowData = new FlowData { ProjectData = data, DesignSurfaceData = new DesignSurfaceData { DrawingObjects = objects } };
                break;
            case ProjectType.CodeApp:
                data.CodeFrameData!.Code = CodeAppTemplate(lang);
                file.CodeData = new CodeData { ProjectData = data };
                break;
            case ProjectType.CodeFunc:
                (data.CodeFrameData!.Global, data.CodeFrameData.Code) = CodeFuncTemplate(name, lang);
                file.CodeData = new CodeData { ProjectData = data };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
        var session = new ProjectSession(file, full, install, type);
        session.Save();
        return session;
    }

    public const int IconWidth = 32;
    public const int IconHeight = 32;

    /// <summary>FlowView.AddCtrlIcon(Start, (64, 64)): a 32x32 Start icon centred there with one Start port (top right).</summary>
    public static GraphicObject NewStartIcon(EncodedImage? image) =>
        NewIcon(IconType.Start, new Point(IconWidth * 2 - IconWidth / 2, IconHeight * 2 - IconHeight / 2), image, new DataObject());

    /// <summary>IconDraw(pos, img, type) with InitPorts, wrapped in a GraphicObject with the given payload.</summary>
    public static GraphicObject NewIcon(IconType type, Point position, EncodedImage? image, DataObject data)
    {
        var go = new GraphicObject { DataObject = data, CodeObject = new CodeObject() };
        var draw = new IconDraw { Parent = go, Position = position, Size = new Size(IconWidth, IconHeight), Image = image, IconType = type, AutoSize = false };
        foreach (var (portType, dir) in PortLayout(type)) draw.Ports!.Add(new Port { Parent = draw, Type = portType, PortDirection = dir });
        go.DrawObject = draw;
        return go;
    }

    /// <summary>IconDraw.InitPorts.</summary>
    public static IReadOnlyList<(PortType Type, PortDirection Direction)> PortLayout(IconType type) => type switch
    {
        IconType.Calculate or IconType.Continue or IconType.Break or IconType.Return or IconType.CodeEditor or IconType.Function =>
            [(PortType.In, PortDirection.TopLeft), (PortType.Out, PortDirection.TopRight)],
        IconType.BeginIf => [(PortType.In, PortDirection.TopLeft), (PortType.BeginIf, PortDirection.TopRight), (PortType.BeginElse, PortDirection.BottomRight)],
        IconType.EndIf => [(PortType.EndIf, PortDirection.TopLeft), (PortType.EndElse, PortDirection.BottomLeft), (PortType.Out, PortDirection.TopRight)],
        IconType.BeginWhile => [(PortType.In, PortDirection.TopLeft), (PortType.BeginWhile, PortDirection.TopRight)],
        IconType.EndWhile => [(PortType.EndWhile, PortDirection.TopLeft), (PortType.Out, PortDirection.TopRight)],
        IconType.BeginFor => [(PortType.In, PortDirection.TopLeft), (PortType.BeginFor, PortDirection.TopRight)],
        IconType.EndFor => [(PortType.EndFor, PortDirection.TopLeft), (PortType.Out, PortDirection.TopRight)],
        IconType.Start => [(PortType.Start, PortDirection.TopRight)],
        _ => [],
    };

    /// <summary>FrmCodeApp.InitCode (no Code.xml globals for the targets).</summary>
    public static string CodeAppTemplate(UiLanguage lang, string entry = "int main(void)")
    {
        var head = lang switch { UiLanguage.CHS => "//应用程序模板 ", UiLanguage.CHT => "//應用程序模板 ", _ => "//Application temple " };
        return head + "\r\n" + "\r\n" + entry + "\r\n{\r\n\r\n}\r\n";
    }

    /// <summary>FrmCodeFunc.initSc: (Global, Code).</summary>
    public static (string Global, string Code) CodeFuncTemplate(string name, UiLanguage lang)
    {
        var up = name.ToUpperInvariant();
        var (g, c) = lang switch
        {
            UiLanguage.CHS => ("//引用代码区", "//编程区"),
            UiLanguage.CHT => ("//引用代碼區", "//編程區"),
            _ => ("//global area", "//code area"),
        };
        return (g + "\r\n#ifndef _" + up + "_\r\n#define _" + up + "_\r\n", c + "\r\n{\r\n\r\n}\r\n#endif\r\n");
    }
}
