using RoboExp.Core.Model.Graphics;

namespace RoboExp.Core.Model;

/// <summary>
/// Assembly display names written into the file's BinaryLibrary records. The original BinaryFormatter resolves types
/// by simple assembly name, so any RoboExp version loads; round-tripped files keep the names they were read with.
/// </summary>
public sealed record RcuAssemblyNames(string RoboExp, string SoftType, string SystemDrawing)
{
    /// <summary>The decompiled reference build (RoboExp 6.0.8.31673 on .NET Framework 4).</summary>
    public static RcuAssemblyNames Default { get; } = new(
        "RoboExp, Version=6.0.8.31673, Culture=neutral, PublicKeyToken=null",
        "SoftType, Version=1.0.2.0, Culture=neutral, PublicKeyToken=null",
        "System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a");

    /// <summary>
    /// System.Drawing 1.0.5000 (.NET 1.1) serialized <c>Color</c> as value, knownColor, state, name;
    /// 2.0/4.0 as name, value, knownColor, state.
    /// </summary>
    internal bool LegacyColorLayout => SystemDrawing.Contains("Version=1.", StringComparison.Ordinal);
}

/// <summary><c>RoboExp.RCUFile</c>: root object of a .rcu project file.</summary>
public sealed class RcuFile : RcuObject
{
    /// <summary>Serialized <c>m_Version</c>.</summary>
    public int Version { get; set; } = 100;

    public ProjectType ProjectType { get; set; }

    /// <summary>Set for flowchart projects (<see cref="Model.ProjectType.FlowApp"/>/<see cref="Model.ProjectType.FlowFunc"/>).</summary>
    public FlowData? FlowData { get; set; }

    /// <summary>Set for code projects (<see cref="Model.ProjectType.CodeApp"/>/<see cref="Model.ProjectType.CodeFunc"/>).</summary>
    public CodeData? CodeData { get; set; }

    /// <summary>Assembly names used when writing; set by the reader to the file's own.</summary>
    public RcuAssemblyNames Assemblies { get; set; } = RcuAssemblyNames.Default;

    /// <summary>The project data of whichever of <see cref="FlowData"/>/<see cref="CodeData"/> is present.</summary>
    public ProjectData? ProjectData => FlowData?.ProjectData ?? CodeData?.ProjectData;
}

/// <summary><c>RoboExp.BaseData</c> (abstract): members <c>m_BaseDataVersion, m_ProjectData</c>.</summary>
public abstract class BaseData : RcuObject
{
    public int BaseDataVersion { get; set; } = 100;
    public ProjectData? ProjectData { get; set; }

    /// <summary>Serialized <c>m_Version</c> of the concrete class.</summary>
    public int Version { get; set; } = 100;
}

/// <summary><c>RoboExp.FlowData</c>: flowchart project (adds <c>m_Version, m_DesignSurfaceData</c>).</summary>
public sealed class FlowData : BaseData
{
    public DesignSurfaceData? DesignSurfaceData { get; set; }
}

/// <summary><c>RoboExp.CodeData</c>: code project (adds <c>m_Version</c>).</summary>
public sealed class CodeData : BaseData;

/// <summary><c>RoboExp.FunctionData</c> (adds <c>m_Version, m_FuncHead, m_FuncDeclare, m_FuncBody</c>).</summary>
public sealed class FunctionData : BaseData
{
    public string? FuncHead { get; set; } = "";
    public string? FuncDeclare { get; set; } = "";
    public string? FuncBody { get; set; } = "";
}

/// <summary><c>RoboExp.ProjectData</c>.</summary>
public sealed class ProjectData : RcuObject
{
    /// <summary>Serialized <c>m_ProjectDataVersion</c>.</summary>
    public int ProjectDataVersion { get; set; } = 100;

    public ProjectInfo? ProjectInfo { get; set; }

    /// <summary>Global variables (<see cref="VarStruct"/>).</summary>
    public EventArrayList<VarStruct>? VarList { get; set; } = new();

    public HardwareTable? HardwareTable { get; set; } = new();

    /// <summary>Signatures of the library/user functions this project calls.</summary>
    public EventArrayList<ProjectInfo>? UsedProjectInfoList { get; set; } = new();

    public CodeFrameData? CodeFrameData { get; set; } = new();

    /// <summary>
    /// Original <c>ProjectData.GetUsedProjectInfo</c>: the used function signature with this exact (case-sensitive) name.
    /// </summary>
    public ProjectInfo? GetUsedProjectInfo(string projectName) =>
        UsedProjectInfoList?.FirstOrDefault(p => string.Equals(p.ProjectName, projectName, StringComparison.Ordinal));
}

/// <summary>
/// <c>RoboExp.CodeFrameData</c>: generated/stored C source. <see cref="AllCode"/> is the complete program the
/// original writes to <c>&lt;project&gt;.c</c>. Members <c>m_OldVersion, m_Global, m_Func, m_Code, m_AllCode</c>.
/// </summary>
public sealed class CodeFrameData : RcuObject
{
    /// <summary>Serialized <c>m_OldVersion</c> (the class persists its old version, not <c>m_Version</c>).</summary>
    public int OldVersion { get; set; } = 100;

    public string? Global { get; set; } = "";
    public string? Func { get; set; } = "";
    public string? Code { get; set; } = "";
    public string? AllCode { get; set; } = "";
}

/// <summary><c>RoboExp.DesignSurfaceData</c>: the flowchart canvas.</summary>
public sealed class DesignSurfaceData : RcuObject
{
    public int Version { get; set; } = 100;
    public Point ScrollOffset { get; set; }
    public GraphicObjectCollection? DrawingObjects { get; set; }
}
