using System.Formats.Nrbf;
using RoboExp.Core.Install;

namespace RoboExp.Core.Hardware;

/// <summary>Which pin list of the controller picture a pin index refers to (original <c>RectangleType</c>).</summary>
public enum PinKind { Ctrl, Gnd, Vcc }

/// <summary>A pin of the controller picture (<c>RCUInfo.PinInfo</c>): caption and top-left corner in picture pixels.</summary>
public sealed record BoardPin(string Name, int X, int Y);

/// <summary>
/// A predefined pin group (<c>RCUInfo.PinTeam</c> of One/Two/Three/FourPinList): the control pins of a port plus its
/// GND/Vcc pins (indices into <see cref="RcuBoard.CtrlPins"/>/<see cref="RcuBoard.GndPins"/>/<see cref="RcuBoard.VccPins"/>).
/// </summary>
public sealed record PinTeamDef(IReadOnlyList<int> Ctrl, IReadOnlyList<int> Gnd, IReadOnlyList<int> Vcc)
{
    public IReadOnlyList<int> Get(PinKind kind) => kind switch { PinKind.Ctrl => Ctrl, PinKind.Gnd => Gnd, _ => Vcc };
}

/// <summary>
/// The controller picture and pin layout used by the hardware dialogs (original <c>RCUInfo</c>, element 0 of
/// <c>Robots/&lt;TYPE&gt;/Resources/RCUInfo.dat</c>; only X3RCU/X4RCU ship it). Code generation only needs the MCU
/// port/pin numbers and uses <see cref="CodeGen.RcuInfo"/>; this reader adds the geometry, picture and pin teams.
/// </summary>
public sealed class RcuBoard
{
    public string Name { get; init; } = "";

    /// <summary>"Circle", "Square" or "Oblong" (the original draws anything but Circle as a rectangle).</summary>
    public string PinGraph { get; init; } = "";

    /// <summary>Size of every pin rectangle (<c>m_Width</c>/<c>m_Height</c>).</summary>
    public int PinWidth { get; init; }
    public int PinHeight { get; init; }

    /// <summary>Encoded picture bytes (BMP for X3RCU, JPEG for X4RCU); null when absent.</summary>
    public byte[]? Picture { get; init; }

    public IReadOnlyList<BoardPin> CtrlPins { get; init; } = [];
    public IReadOnlyList<BoardPin> GndPins { get; init; } = [];
    public IReadOnlyList<BoardPin> VccPins { get; init; } = [];

    /// <summary>Groups of control pins that are electrically the same (<c>m_SamePinList</c>).</summary>
    public IReadOnlyList<IReadOnlyList<int>> SamePinList { get; init; } = [];

    public IReadOnlyList<PinTeamDef> OnePinList { get; init; } = [];
    public IReadOnlyList<PinTeamDef> TwoPinList { get; init; } = [];
    public IReadOnlyList<PinTeamDef> ThreePinList { get; init; } = [];
    public IReadOnlyList<PinTeamDef> FourPinList { get; init; } = [];

    public bool IsCircle => PinGraph == "Circle";

    public IReadOnlyList<BoardPin> Pins(PinKind kind) => kind switch { PinKind.Ctrl => CtrlPins, PinKind.Gnd => GndPins, _ => VccPins };

    /// <summary>The team list for a hardware type's pin count (empty for counts outside 1..4).</summary>
    public IReadOnlyList<PinTeamDef> TeamsFor(int pinCount) => pinCount switch
    {
        1 => OnePinList,
        2 => TwoPinList,
        3 => ThreePinList,
        4 => FourPinList,
        _ => [],
    };

    /// <summary>
    /// HardwareInstFrm.GetTypeAndNum: the pin under a picture point, searching Ctrl, then GND, then Vcc
    /// (pin rectangles are <see cref="PinWidth"/> × <see cref="PinHeight"/>, right/bottom edge exclusive).
    /// </summary>
    public (PinKind Kind, int Index)? HitTest(double x, double y)
    {
        foreach (var kind in new[] { PinKind.Ctrl, PinKind.Gnd, PinKind.Vcc })
        {
            var pins = Pins(kind);
            for (var i = 0; i < pins.Count; i++)
            {
                var p = pins[i];
                if (x >= p.X && x < p.X + PinWidth && y >= p.Y && y < p.Y + PinHeight) return (kind, i);
            }
        }
        return null;
    }

    public static string PathFor(VendorInstall install, RobotType type) =>
        Path.Combine(install.RobotDirectory(type), "Resources", "RCUInfo.dat");

    /// <summary>DataManager.Init: element 0 of RCUInfo.dat; null when the type ships none.</summary>
    public static RcuBoard? Load(VendorInstall install, RobotType type)
    {
        var path = PathFor(install, type);
        if (!File.Exists(path)) return null;
        using var s = File.OpenRead(path);
        return Read(s);
    }

    public static RcuBoard? Read(Stream stream)
    {
        if (NrbfDecoder.Decode(stream) is not ClassRecord list) throw new InvalidDataException("RCUInfo.dat: root is not an ArrayList");
        var items = Items(list);
        if (items.Count == 0 || items[0] is not ClassRecord info) return null;

        byte[]? picture = null;
        if (info.GetClassRecord("m_RCUPicture") is { } bmp && bmp.GetArrayRecord("Data") is SZArrayRecord<byte> data)
            picture = data.GetArray();

        return new RcuBoard
        {
            Name = info.GetString("m_RCUName") ?? "",
            PinGraph = info.GetString("m_PinGraph") ?? "",
            PinWidth = info.GetInt32("m_Width"),
            PinHeight = info.GetInt32("m_Height"),
            Picture = picture,
            CtrlPins = PinList(info, "m_CtrlPinList"),
            GndPins = PinList(info, "m_GNDPinList"),
            VccPins = PinList(info, "m_VccPinList"),
            SamePinList = ListOf(info, "m_SamePinList", r => (IReadOnlyList<int>)Ints(r as ClassRecord)),
            OnePinList = ListOf(info, "m_OnePinList", Team),
            TwoPinList = ListOf(info, "m_TwoPinList", Team),
            ThreePinList = ListOf(info, "m_ThreePinList", Team),
            FourPinList = ListOf(info, "m_FourPinList", Team),
        };
    }

    private static PinTeamDef Team(SerializationRecord? r)
    {
        var c = r as ClassRecord;
        return new PinTeamDef(Ints(c?.GetClassRecord("CtrlPinNumList")), Ints(c?.GetClassRecord("GNDPinNumList")), Ints(c?.GetClassRecord("VccPinNumList")));
    }

    private static List<BoardPin> PinList(ClassRecord info, string member) => ListOf(info, member, r =>
    {
        var pin = r as ClassRecord;
        var loc = pin?.GetClassRecord("m_Location");
        return new BoardPin((pin?.GetString("m_PinName") ?? "").Trim(), loc?.GetInt32("x") ?? 0, loc?.GetInt32("y") ?? 0);
    });

    private static List<T> ListOf<T>(ClassRecord info, string member, Func<SerializationRecord?, T> map) =>
        info.GetClassRecord(member) is { } list ? Items(list).Select(map).ToList() : [];

    private static int[] Ints(ClassRecord? arrayList) =>
        arrayList == null ? [] : Items(arrayList).Select(r => r is PrimitiveTypeRecord { Value: int v } ? v : -1).ToArray();

    private static List<SerializationRecord?> Items(ClassRecord arrayList)
    {
        var size = arrayList.GetInt32("_size");
        var items = arrayList.GetArrayRecord("_items") as SZArrayRecord<SerializationRecord>;
        return (items?.GetArray() ?? []).Take(size).ToList();
    }
}
