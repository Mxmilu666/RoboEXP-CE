namespace RoboExp.Core.Model;

/// <summary>
/// Controller family. Values match the original <c>RoboExp.SoftwareType</c> (assembly <c>SoftType</c>);
/// they are persisted as Int32 inside .rcu files, so NEVER renumber.
/// </summary>
public enum SoftwareType
{
    Default = 0,
    Developer = 1,
    Demo = 2,
    JiMu8 = 3,
    JiMu16 = 4,
    IQBug = 5,
    Tank = 6,
    JiMu8a = 7,
    Football = 8,
    SuperRCU = 9,
    BasicRCU = 10,
    XRCU = 11,
    ERCU = 12,
    QRCU = 13,
    X1RCU = 14,
    X2RCU = 15,
    E2RCU = 16,
    IQrobot = 17,
    X3RCU = 18,
    E3RCU = 19,
    MRCU = 20,
    IQ2Robot = 21,
    EV5 = 22,
    M2RCU = 23,
    E6RCU = 24,
    X4RCU = 25,
    C1RCU = 26,
    M6RCU = 27,
    A3RCU = 28,
    C6RCU = 29,
    E7RCU = 30,
    M7RCU = 31,
    RCJ = 50,
    RCJC = 51,
    RCJJava = 52,
    RCJCSharp = 53,
    RCJPascal = 54,
}
