using RoboExp.Core.Model;

namespace RoboExp.Core.Install;

/// <summary>A controller supported by this port. <see cref="Name"/> is the vendor directory / JMCompiler.xml group name.</summary>
public sealed record RobotType(SoftwareType SoftwareType)
{
    public string Name => SoftwareType.ToString();

    /// <summary>
    /// Controllers built with the arm-none-eabi toolchain that also ship headers/libs under <c>Robots/</c>.
    /// EV5, IQrobot and X2RCU have JMCompiler.xml entries but no Robots directory, so they cannot be built.
    /// X1RCU (avr-gcc 3.4.3) and QRCU (arm-elf 4.1.1) are out of scope for the native ARM port.
    /// </summary>
    public static IReadOnlyList<RobotType> Supported { get; } = new[]
    {
        SoftwareType.MRCU, SoftwareType.M2RCU, SoftwareType.M6RCU,
        SoftwareType.E2RCU, SoftwareType.E3RCU, SoftwareType.E6RCU, SoftwareType.E7RCU,
        SoftwareType.C1RCU, SoftwareType.C6RCU,
        SoftwareType.X3RCU, SoftwareType.X4RCU,
        SoftwareType.IQ2Robot,
    }.Select(t => new RobotType(t)).ToArray();

    public static RobotType Parse(string name) =>
        Supported.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unsupported robot type '{name}'. Supported: {string.Join(", ", Supported.Select(t => t.Name))}");
}
