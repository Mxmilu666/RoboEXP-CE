namespace RoboExp.Core.Model;

// Enum values are persisted as Int32 (member value__) inside .rcu files: NEVER renumber.

/// <summary><c>RoboExp.ProjectType</c>.</summary>
public enum ProjectType
{
    Default = 0,
    FlowApp = 1,
    CodeApp = 2,
    FlowFunc = 3,
    CodeFunc = 4,
}

/// <summary><c>RoboExp.OuterType</c> (assembly SoftType).</summary>
public enum OuterType
{
    Entity = 0,
    Simulation = 1,
}

/// <summary><c>RoboExp.IconType</c>: kind of flowchart icon.</summary>
public enum IconType
{
    Default = 0,
    Start = 1,
    BeginIf = 2,
    EndIf = 3,
    BeginWhile = 4,
    EndWhile = 5,
    BeginFor = 6,
    EndFor = 7,
    Calculate = 8,
    Continue = 9,
    Break = 10,
    Return = 11,
    CodeEditor = 12,
    Function = 13,
    Task = 14,
}
