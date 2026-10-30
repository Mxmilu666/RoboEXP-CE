namespace RoboExp.Core.Model;

/// <summary><c>RoboExp.HardwareTable</c>: the project's hardware configuration (sensors/motors on controller pins).</summary>
public sealed class HardwareTable : RcuObject
{
    public int Version { get; set; } = 100;
    public EventArrayList<HardwareItem>? HardwareItemList { get; set; } = new();
    public string? Description { get; set; } = "";
    public SoftwareType SoftwareType { get; set; }
}

/// <summary><c>RoboExp.HardwareItem</c>: one hardware type with its configured instances.</summary>
public sealed class HardwareItem : RcuObject
{
    public int Version { get; set; } = 100;
    public HardwareType? HardwareType { get; set; } = new();
    public ArrayList<HardwareInstance>? InstanceList { get; set; } = new();
}

/// <summary>
/// <c>RoboExp.HardwareType</c>. Note the serialized name of <see cref="PinDescriptionList"/> is <c>m_PinDescription</c>.
/// </summary>
public sealed class HardwareType : RcuObject
{
    public int Version { get; set; } = 101;
    public string? Name { get; set; }
    public int PinCount { get; set; }
    public ArrayList<string?>? PinDescriptionList { get; set; } = new();
    public bool IsDefault { get; set; } = true;
    public string? Description { get; set; }
    public ArrayList<int>? CtrlLimitList { get; set; } = new();
    public string? NickName { get; set; } = "";
    public ArrayList<int>? GNDLimitList { get; set; } = new();
    public ArrayList<int>? VccLimitList { get; set; } = new();
}

/// <summary><c>RoboExp.HardwareInstance</c>: a named hardware instance and the controller pins it uses.</summary>
public sealed class HardwareInstance : RcuObject
{
    public int Version { get; set; } = 101;
    public string? TypeName { get; set; } = "";
    public string? Name { get; set; } = "";
    public PinTeam? PinTeam { get; set; } = new();
    public int TypeNum { get; set; }
}

/// <summary><c>RoboExp.RCUInfo+PinTeam</c>: pin numbers of an instance (no version member is serialized).</summary>
public sealed class PinTeam : RcuObject
{
    public ArrayList<int>? CtrlPinNumList { get; set; } = new();
    public ArrayList<int>? GNDPinNumList { get; set; } = new();
    public ArrayList<int>? VccPinNumList { get; set; } = new();
}
