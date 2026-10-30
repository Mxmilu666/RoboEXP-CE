using RoboExp.Core.Model;

namespace RoboExp.Device.Protocol;

/// <summary>
/// DownloadX1RCU.CopyToX1RCU drive detection: a ready volume whose label OR total size identifies the controller.
/// Deviation: the original accepted an "E7-RCU" label for every type (operator precedence bug); here only for E7RCU.
/// </summary>
internal static class DriveCriteria
{
    private static readonly Dictionary<SoftwareType, (string Label, long[] Sizes)> Table = new()
    {
        [SoftwareType.X1RCU] = ("X1-RCU", [385024]),
        [SoftwareType.X2RCU] = ("X2-RCU", [2080768]),
        [SoftwareType.X3RCU] = ("X3-RCU", [6262784, 8359936]),
        [SoftwareType.X4RCU] = ("X4-RCU", [16674816]),
        [SoftwareType.E2RCU] = ("E2-RCU", [176128]),
        [SoftwareType.E3RCU] = ("E3-RCU", [6262784]),
        [SoftwareType.E6RCU] = ("E6-RCU", [14393344]),
        [SoftwareType.E7RCU] = ("E7-RCU", [126779392]),
        [SoftwareType.M7RCU] = ("M7-RCU", [126779392]),
        [SoftwareType.M6RCU] = ("M6-RCU", [6008832]),
        [SoftwareType.C6RCU] = ("C6-RCU", [4960256]),
        [SoftwareType.MRCU] = ("M-RCU", [176128]),
        [SoftwareType.M2RCU] = ("M2-RCU", [176128]),
        [SoftwareType.IQrobot] = ("IQ-Robot", [131072]),
        [SoftwareType.IQ2Robot] = ("IQ2-Robot", [131072]),
    };

    /// <summary>C1RCU has no drive entry in the original, so it never matches.</summary>
    public static bool Matches(SoftwareType type, string label, long totalSize) =>
        Table.TryGetValue(type, out var e) && (label == e.Label || e.Sizes.Contains(totalSize));

    public const int CopyAttempts = 5;
}
