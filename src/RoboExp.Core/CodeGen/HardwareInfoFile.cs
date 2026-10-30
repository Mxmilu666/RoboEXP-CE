using System.Text;
using RoboExp.Core.Build;
using RoboExp.Core.Model;

namespace RoboExp.Core.CodeGen;

/// <summary>HardwareInfo.c and HardwareInstance.GetHardwareInfo.</summary>
public static class HardwareInfoFile
{
    /// <summary>The task1 stub with the original's bare LFs and stray spaces.</summary>
    public const string Task1Stub = "#ifndef _TASK1_ \n#define _TASK1_ \nvoid task1(){}\n #endif \n";

    private static readonly SoftwareType[] NibbleTypes =
    [
        SoftwareType.X1RCU, SoftwareType.X2RCU, SoftwareType.X3RCU, SoftwareType.X4RCU, SoftwareType.E3RCU, SoftwareType.E6RCU,
        SoftwareType.E7RCU, SoftwareType.M7RCU, SoftwareType.M6RCU, SoftwareType.C6RCU, SoftwareType.C1RCU, SoftwareType.EV5,
        SoftwareType.MRCU, SoftwareType.M2RCU, SoftwareType.E2RCU, SoftwareType.IQrobot, SoftwareType.IQ2Robot,
    ];

    /// <summary>
    /// BaseView.CreateHardwareInfoFile (written on every save, so the final on-disk content; MainForm's compile-time X3/X4
    /// variant is always overwritten by it): type blocks, then the task1 stub (X4/E6/E7/M7 apps whose AllCode lacks "task1"),
    /// then instance defines.
    /// </summary>
    public static string Create(ProjectData data, SoftwareType type, RcuInfo rcuInfo)
    {
        var text = new StringBuilder();
        var pt = data.ProjectInfo?.ProjectType;
        if (type is SoftwareType.X4RCU or SoftwareType.E6RCU or SoftwareType.E7RCU or SoftwareType.M7RCU
            && pt is ProjectType.FlowApp or ProjectType.CodeApp
            && !(data.CodeFrameData?.AllCode ?? "").Contains("task1", StringComparison.Ordinal))
            text.Append(Task1Stub);
        var items = data.HardwareTable?.HardwareItemList;
        if (items == null || items.Count < 1) return text.ToString();
        var types = new StringBuilder();
        foreach (var item in items)
        {
            var name = item.HardwareType?.Name;
            types.Append("#ifndef ").Append(name).Append("\r\n#define ").Append(name).Append(" unsigned long\r\n#endif\r\n");
            foreach (var inst in (IEnumerable<HardwareInstance>?)item.InstanceList ?? [])
                text.Append("#ifndef ").Append(inst.Name).Append("\r\n#define ").Append(inst.Name).Append(' ')
                    .Append(GetHardwareInfo(inst, rcuInfo, type)).Append("\r\n#endif\r\n");
        }
        return types.Append(text).ToString();
    }

    /// <summary>
    /// HardwareInstance.GetHardwareInfo: for ctrl pins 3..0 pack (pin, port) as 4+3 bits for the ARM targets (QRCU: 5-bit pin
    /// number + 1 zero bit; others 3+3), shift 4 (others 8) and OR the type number.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// A ctrl pin index outside <see cref="RcuInfo.CtrlPinList"/> (the original throws an index exception from Save too;
    /// happens for instances in projects of types that ship no RCUInfo.dat).
    /// </exception>
    public static uint GetHardwareInfo(HardwareInstance inst, RcuInfo rcuInfo, SoftwareType type)
    {
        uint num = 0;
        var pins = (IReadOnlyList<int>?)inst.PinTeam?.CtrlPinNumList ?? [];
        for (var i = 3; i >= 0; i--)
        {
            byte port = 0, pin = 0;
            if (i < pins.Count)
            {
                if ((uint)pins[i] >= (uint)rcuInfo.CtrlPinList.Count)
                    throw new InvalidDataException($"Hardware instance {inst.Name}: ctrl pin {pins[i]} is not defined in the {type} RCUInfo ({rcuInfo.CtrlPinList.Count} pins)");
                var hp = rcuInfo.CtrlPinList[pins[i]];
                port = (byte)hp.PortNum;
                pin = (byte)hp.PinNum;
            }
            unchecked
            {
                if (type == SoftwareType.QRCU)
                {
                    num = (num << 5) | (i < pins.Count ? PortPinToPin(port, pin) : 0u);
                    num <<= 1;
                }
                else if (NibbleTypes.Contains(type))
                {
                    num = (num << 4) | pin;
                    num = (num << 3) | port;
                }
                else
                {
                    num = (num << 3) | pin;
                    num = (num << 3) | port;
                }
            }
        }
        unchecked
        {
            num = NibbleTypes.Contains(type) ? num << 4 : num << 8;
            return num | (byte)inst.TypeNum;
        }
    }

    /// <summary>HardwareInstance.PortPinToPin (QRCU pin numbering).</summary>
    private static uint PortPinToPin(byte port, byte pin) => (port, pin) switch
    {
        (0, <= 7) => 17u + pin,
        (1, 0) => 12, (1, 1) => 11, (1, 2) => 14, (1, 3) => 13,
        (2, 0) => 4, (2, 1) => 6, (2, 2) => 3, (2, 3) => 5, (2, 4) => 27, (2, 5) => 2, (2, 6) => 29, (2, 7) => 30,
        (3, 4) => 8, (3, 5) => 7, (3, 6) => 10, (3, 7) => 9,
        _ => 0,
    };

    /// <summary>PublicMethod.WriteTextFile: Encoding.Default (GBK on zh-CN Windows), no BOM, text verbatim.</summary>
    public static void WriteTextFile(string path, string text)
    {
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, FileAttributes.Normal);
        File.WriteAllBytes(path, Gbk.Encoding.GetBytes(text));
    }

    public static Encoding TextEncoding => Gbk.Encoding;
}
