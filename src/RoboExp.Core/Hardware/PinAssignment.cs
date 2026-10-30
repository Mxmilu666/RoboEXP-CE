using RoboExp.Core.Model;

namespace RoboExp.Core.Hardware;

/// <summary>
/// State and rules of the hardware instance dialog (original HardwareInstFrm) independent of the UI: the pins the
/// type may not use (<c>Ctrl/GND/VccLimitList</c>), the pins already taken by other instances, the hover candidate
/// and the current selection.
/// <para>Deviation (SPEC-EDITOR §10): the original only drew taken pins crossed out but still let them be picked;
/// here a candidate touching a taken pin (or a SamePinList twin of one) is disabled like a limited pin.</para>
/// </summary>
public sealed class PinAssignment
{
    private readonly HashSet<int>[] _limits = [[], [], []];
    private readonly HashSet<int>[] _used = [[], [], []];

    /// <param name="table">The combined hardware table (all instances that occupy pins).</param>
    /// <param name="editingInstanceName">Name of the instance being edited (its own pins are free), or null when adding.</param>
    public PinAssignment(RcuBoard board, HardwareType type, HardwareTable table, string? editingInstanceName = null, PinTeam? initial = null)
    {
        Board = board;
        Type = type;
        Table = table;
        EditingInstanceName = editingInstanceName;
        AddInRange(_limits[(int)PinKind.Ctrl], type.CtrlLimitList, board.CtrlPins.Count);
        AddInRange(_limits[(int)PinKind.Gnd], type.GNDLimitList, board.GndPins.Count);
        AddInRange(_limits[(int)PinKind.Vcc], type.VccLimitList, board.VccPins.Count);
        ComputeUsed();

        if (initial?.CtrlPinNumList is { Count: > 0 } ctrl)
        {
            Ctrl = [.. ctrl];
            Gnd = [.. initial.GNDPinNumList ?? []];
            Vcc = [.. initial.VccPinNumList ?? []];
        }
        else
        {
            Ctrl = Enumerable.Repeat(-1, Math.Max(0, type.PinCount)).ToList();
        }
    }

    public RcuBoard Board { get; }
    public HardwareType Type { get; }
    public HardwareTable Table { get; }
    public string? EditingInstanceName { get; }

    /// <summary>Selected control pins, one per type pin (-1 = unassigned).</summary>
    public List<int> Ctrl { get; private set; }
    public List<int> Gnd { get; private set; } = [];
    public List<int> Vcc { get; private set; } = [];

    /// <summary>Pin slot a click assigns to when the type does not use the default pin order (TrePins selection).</summary>
    public int SelectedSlot { get; set; }

    public IReadOnlyCollection<int> Limits(PinKind kind) => _limits[(int)kind];
    public IReadOnlyCollection<int> Used(PinKind kind) => _used[(int)kind];

    public IReadOnlyList<int> Selected(PinKind kind) => kind switch { PinKind.Ctrl => Ctrl, PinKind.Gnd => Gnd, _ => Vcc };

    public bool IsLimited(PinKind kind, int index) => _limits[(int)kind].Contains(index);
    public bool IsUsed(PinKind kind, int index) => _used[(int)kind].Contains(index);

    /// <summary>isEnabledPinTeam, plus taken pins.</summary>
    public bool IsTeamAvailable(PinTeamDef team)
    {
        foreach (var kind in new[] { PinKind.Ctrl, PinKind.Gnd, PinKind.Vcc })
            foreach (var n in team.Get(kind))
                if (IsLimited(kind, n) || IsUsed(kind, n)) return false;
        return true;
    }

    /// <summary>
    /// PtbHardware_MouseMove: what a click on pin <paramref name="index"/> of <paramref name="kind"/> would select, or
    /// null when nothing selectable is there. Default order: the board's team for the type's pin count (1/2 pins: the
    /// first team containing the pin decides; 3/4 pins: the last available one). Otherwise: that single control pin,
    /// unless another slot already holds it or a pin connected to it.
    /// </summary>
    public PinTeamDef? CandidateAt(PinKind kind, int index)
    {
        if (Type.IsDefault)
        {
            var teams = Board.TeamsFor(Type.PinCount);
            if (Type.PinCount <= 2)
            {
                foreach (var team in teams)
                {
                    var list = team.Get(kind);
                    if (kind == PinKind.Ctrl ? !list.Contains(index) : list.Count == 0 || list[0] != index) continue;
                    return IsTeamAvailable(team) ? team : null;
                }
                return null;
            }
            for (var i = teams.Count - 1; i >= 0; i--)
            {
                var team = teams[i];
                var list = team.Get(kind);
                var hit = kind == PinKind.Ctrl ? list.Contains(index) : list.Take(2).Contains(index);
                if (hit && IsTeamAvailable(team)) return team;
            }
            return null;
        }

        if (kind != PinKind.Ctrl || IsLimited(kind, index) || IsUsed(kind, index)) return null;
        for (var i = 0; i < Ctrl.Count; i++)
        {
            var n = Ctrl[i];
            if (n >= 0 && i != SelectedSlot && (n == index || AreSamePins(n, index))) return null;
        }
        return new PinTeamDef([index], [], []);
    }

    /// <summary>PtbHardware_Click with a candidate from <see cref="CandidateAt"/>.</summary>
    public void Apply(PinTeamDef candidate)
    {
        if (Type.IsDefault)
        {
            Ctrl = [.. candidate.Ctrl];
            Gnd = [.. candidate.Gnd];
            Vcc = [.. candidate.Vcc];
            return;
        }
        if (Ctrl.Count == 0) Ctrl = Enumerable.Repeat(-1, Math.Max(0, Type.PinCount)).ToList();
        if (Ctrl.Count == 0) return;
        var slot = SelectedSlot < 0 || SelectedSlot >= Ctrl.Count ? 0 : SelectedSlot;
        Ctrl[slot] = candidate.Ctrl[0];
    }

    /// <summary>Slot captions of the pin list: "Pin0: No.A1Sx pin" / "Pin1: Null".</summary>
    public IReadOnlyList<string> SlotCaptions() =>
        Enumerable.Range(0, Math.Max(Ctrl.Count, Math.Max(0, Type.PinCount))).Select(i =>
        {
            var n = i < Ctrl.Count ? Ctrl[i] : -1;
            return n >= 0 && n < Board.CtrlPins.Count ? $"Pin{i}: No.{Board.CtrlPins[n].Name} pin" : $"Pin{i}: Null";
        }).ToList();

    /// <summary>OK check of the pins: every control pin set and on the board. Returns the Xwb message key or null.</summary>
    public string? ValidatePins()
    {
        if (Ctrl.Count != Type.PinCount) return "HardwareInstFrm_ValueIsNull";
        foreach (var n in Ctrl)
            if (n < 0 || n >= Board.CtrlPins.Count) return "HardwareInstFrm_ValueIsNull";
        return null;
    }

    public PinTeam ToPinTeam() => new()
    {
        CtrlPinNumList = new ArrayList<int>(Ctrl),
        GNDPinNumList = new ArrayList<int>(Gnd),
        VccPinNumList = new ArrayList<int>(Vcc),
    };

    /// <summary>IsEqualPinNum: both pins are in the same SamePinList group.</summary>
    public bool AreSamePins(int a, int b)
    {
        foreach (var group in Board.SamePinList)
            if (group.Contains(a)) return group.Contains(b);
        return false;
    }

    /// <summary>
    /// HardwareInstFrm.BtnOk_Click name rules. <paramref name="text"/> is the part between the type name and the
    /// trailing "_"; the full name is <c>TypeName + text + "_"</c>. Returns the Xwb message key or null when valid.
    /// </summary>
    public static string? ValidateInstanceName(string typeName, string text, HardwareTable table, IEnumerable<VarStruct>? variables)
    {
        if (text.Length == 0) return "HardwareInstFrm_NameIsNull";
        if (!IsAsciiLetterOrDigit(text)) return "HardwareInstFrm_ErrHardwareName";
        var full = FullInstanceName(typeName, text);
        if (HardwareTables.FindInstance(table, full) != null) return "HardwareInstFrm_SameName";
        if (variables != null && variables.Any(v => v.Name == full)) return "HardwareInstFrm_SameVarName";
        return null;
    }

    public static string FullInstanceName(string typeName, string text) => typeName + text + "_";

    /// <summary>The editable middle of an instance name (<c>_MOTOR_M1_</c> of type <c>_MOTOR_</c> → <c>M1</c>), or "".</summary>
    public static string InstanceNameText(string typeName, string? instanceName)
    {
        if (instanceName == null || !instanceName.StartsWith(typeName, StringComparison.Ordinal)) return "";
        var text = instanceName[typeName.Length..];
        return text.EndsWith('_') ? text[..^1] : "";
    }

    /// <summary>HardwareTypeFrm.IsTrueTypeName: letters/digits; "_" + text + "_" must be a new type. Returns the Xwb key or null.</summary>
    public static string? ValidateTypeName(string text, HardwareTable table)
    {
        if (text.Length == 0) return "HardwareTypeFrm_NameIsNull";
        if (!IsAsciiLetterOrDigit(text)) return "HardwareTypeFrm_ErrTypeName";
        if (HardwareTables.ContainsType(table, "_" + text + "_")) return "HardwareTypeFrm_SameName";
        return null;
    }

    public static bool IsAsciiLetterOrDigit(string text) => text.Length > 0 && text.All(char.IsAsciiLetterOrDigit);

    /// <summary>
    /// HardwareTypeFrm.PtbPins_Click: toggles a pin in a type's disabled-pin list. Control pins toggle together with
    /// their SamePinList group (LimitNumAdd/LimitNumRemove).
    /// </summary>
    public static void ToggleLimit(List<int> limits, PinKind kind, int index, RcuBoard board)
    {
        var group = kind == PinKind.Ctrl ? board.SamePinList.FirstOrDefault(g => g.Contains(index)) : null;
        IEnumerable<int> pins = group ?? [index];
        if (limits.Contains(index)) limits.RemoveAll(pins.Contains);
        else limits.AddRange(pins);
    }

    private void ComputeUsed()
    {
        foreach (var item in Table.HardwareItemList ?? [])
        {
            foreach (var inst in item.InstanceList ?? [])
            {
                if (EditingInstanceName != null && inst.Name == EditingInstanceName) continue;
                var team = inst.PinTeam;
                if (team == null) continue;
                AddInRange(_used[(int)PinKind.Gnd], team.GNDPinNumList, Board.GndPins.Count);
                AddInRange(_used[(int)PinKind.Vcc], team.VccPinNumList, Board.VccPins.Count);
                foreach (var n in team.CtrlPinNumList ?? [])
                {
                    if (n < 0 || n >= Board.CtrlPins.Count) continue;
                    _used[(int)PinKind.Ctrl].Add(n);
                    var group = Board.SamePinList.FirstOrDefault(g => g.Contains(n));
                    if (group != null) _used[(int)PinKind.Ctrl].UnionWith(group);
                }
            }
        }
    }

    private static void AddInRange(HashSet<int> set, IEnumerable<int>? values, int count)
    {
        foreach (var v in values ?? [])
            if (v >= 0 && v < count) set.Add(v);
    }
}
