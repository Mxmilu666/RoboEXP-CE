using System.Formats.Nrbf;
using RoboExp.Core.Install;

namespace RoboExp.Core.CodeGen;

/// <summary>A controller pin's MCU port/pin (<c>RoboExp.HardPin</c>).</summary>
public readonly record struct HardPin(int PortNum, int PinNum, int PosNum);

/// <summary>
/// The parts of <c>RoboExp.RCUInfo</c> code generation needs: <see cref="CtrlPinList"/> (index = controller pin number used by
/// <c>PinTeam.CtrlPinNumList</c>). Loaded from <c>Robots/&lt;TYPE&gt;/Resources/RCUInfo.dat</c> (a BinaryFormatter ArrayList of
/// RCUInfo; DataManager takes element 0). Only X3RCU/X4RCU ship it; other types get an empty RCUInfo like the original.
/// </summary>
public sealed class RcuInfo
{
    public static RcuInfo Empty { get; } = new("", []);

    public RcuInfo(string name, IReadOnlyList<HardPin> ctrlPins)
    {
        RcuName = name;
        CtrlPinList = ctrlPins;
    }

    public string RcuName { get; }
    public IReadOnlyList<HardPin> CtrlPinList { get; }

    public static string PathFor(VendorInstall install, RobotType type) =>
        Path.Combine(install.RobotDirectory(type), "Resources", "RCUInfo.dat");

    /// <summary>DataManager.Init: element 0 of RCUInfo.dat, or <see cref="Empty"/> when the file is missing.</summary>
    public static RcuInfo Load(VendorInstall install, RobotType type)
    {
        var path = PathFor(install, type);
        return File.Exists(path) ? Read(path) : Empty;
    }

    public static RcuInfo Read(string path)
    {
        using var s = File.OpenRead(path);
        if (NrbfDecoder.Decode(s) is not ClassRecord list) throw new InvalidDataException($"{path}: root is not an ArrayList");
        var items = ListItems(list);
        if (items.Count == 0 || items[0] is not ClassRecord info) return Empty;
        var pins = new List<HardPin>();
        if (info.GetClassRecord("m_CtrlPinList") is { } ctrl)
        {
            foreach (var r in ListItems(ctrl))
            {
                if (r is ClassRecord pin && pin.GetClassRecord("m_HardwarePin") is { } hp)
                    pins.Add(new HardPin(hp.GetInt32("m_PortNum"), hp.GetInt32("m_PinNum"), hp.GetInt32("m_PosNum")));
                else
                    pins.Add(default);
            }
        }
        return new RcuInfo(info.GetString("m_RCUName") ?? "", pins);
    }

    private static List<SerializationRecord?> ListItems(ClassRecord arrayList)
    {
        var size = arrayList.GetInt32("_size");
        var items = (SZArrayRecord<SerializationRecord>?)arrayList.GetArrayRecord("_items");
        var all = items?.GetArray() ?? [];
        return all.Take(size).ToList();
    }
}
