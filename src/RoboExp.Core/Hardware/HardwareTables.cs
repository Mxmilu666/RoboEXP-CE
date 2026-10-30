using RoboExp.Core.Model;

namespace RoboExp.Core.Hardware;

/// <summary>
/// Hardware table operations of the original HardInfoDlg / MainForm. A project stores only the hardware types it
/// defines plus the system types that have instances; the effective table is <see cref="GetCombinedTable"/>.
/// The mutators below edit the project table in place so that its persisted list objects are kept.
/// </summary>
public static class HardwareTables
{
    /// <summary>
    /// HardInfoDlg.GetCombinedTable: a deep copy of <paramref name="system"/> with the project's types appended and its
    /// instances merged into same-named types (same-named instances replaced in place). Description/SoftwareType come
    /// from the project table.
    /// </summary>
    public static HardwareTable GetCombinedTable(HardwareTable? system, HardwareTable? project)
    {
        if (system == null && project == null) return new HardwareTable();
        if (system == null) return Clone(project!);
        if (project == null) return Clone(system);
        var table = Clone(system);
        table.Description = project.Description;
        table.SoftwareType = project.SoftwareType;
        foreach (var sub in project.HardwareItemList ?? [])
        {
            var main = FindItem(table, sub.HardwareType?.Name);
            if (main == null)
            {
                table.HardwareItemList!.Add(Clone(sub));
                continue;
            }
            main.InstanceList ??= new();
            foreach (var inst in sub.InstanceList ?? [])
            {
                var index = IndexOfInstance(main, inst.Name);
                if (index >= 0) main.InstanceList[index] = Clone(inst);
                else main.InstanceList.Add(Clone(inst));
            }
        }
        return table;
    }

    /// <summary>HardInfoDlg.RemoveSysTypeHasNoInstItem: drops system types without instances from a project table.</summary>
    public static void RemoveSysTypeHasNoInstItem(HardwareTable? system, HardwareTable table)
    {
        var list = table.HardwareItemList;
        if (list == null) return;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var item = list[i];
            if ((item.InstanceList?.Count ?? 0) <= 0 && ContainsType(system, item.HardwareType?.Name)) list.RemoveAt(i);
        }
    }

    /// <summary>MainForm.GetHasInstHardItemList: system types that already have instances (seed of a new project's table).</summary>
    public static List<HardwareItem> GetHasInstHardItemList(HardwareTable? system) =>
        (system?.HardwareItemList ?? []).Where(i => (i.InstanceList?.Count ?? 0) > 0).Select(Clone).ToList();

    /// <summary>
    /// HardInfoDlg.UpdateInfo right tree ("Subroutine using"): the hardware instances the project's called subroutines
    /// use, grouped by type in first-use order, flagged when the type/instance is missing from <paramref name="combined"/>.
    /// </summary>
    public static List<UsedHardwareType> GetSubroutineUsage(ProjectInfo? info, HardwareTable combined)
    {
        var result = new List<UsedHardwareType>();
        foreach (var used in info?.SubFuncUsedHardObjectList ?? [])
        {
            foreach (var inst in used.HardwareInstanceList ?? [])
            {
                var typeName = inst.TypeName ?? "";
                var group = result.FirstOrDefault(g => g.TypeName == typeName);
                if (group == null)
                {
                    var item = FindItem(combined, typeName);
                    group = new UsedHardwareType(typeName, item?.HardwareType?.NickName, item != null, []);
                    result.Add(group);
                }
                var name = inst.Name ?? "";
                if (group.Instances.Any(i => i.Name == name)) continue;
                var defined = group.IsDefined && FindItem(combined, typeName)!.InstanceList?.Any(i => i.Name == name) == true;
                group.Instances.Add(new UsedHardwareInstance(name, defined));
            }
        }
        return result;
    }

    public static bool ContainsType(HardwareTable? table, string? typeName) => FindItem(table, typeName) != null;

    public static HardwareItem? FindItem(HardwareTable? table, string? typeName) =>
        typeName == null ? null : table?.HardwareItemList?.FirstOrDefault(i => string.Equals(i.HardwareType?.Name, typeName, StringComparison.Ordinal));

    public static HardwareInstance? FindInstance(HardwareTable? table, string? instanceName) =>
        table?.HardwareItemList?.SelectMany(i => i.InstanceList ?? []).FirstOrDefault(i => i.Name == instanceName);

    /// <summary>Adds <paramref name="instance"/> to the project table, creating the type item (copied from the system table) if needed.</summary>
    public static void AddInstance(HardwareTable? system, HardwareTable project, HardwareInstance instance)
    {
        var item = EnsureItem(system, project, instance.TypeName);
        (item.InstanceList ??= new()).Add(instance);
    }

    /// <summary>Replaces the pins of an instance; a system-only instance is first copied into the project table.</summary>
    public static void SetInstancePins(HardwareTable? system, HardwareTable project, string instanceName, PinTeam pins)
    {
        var inst = FindInstance(project, instanceName);
        if (inst == null)
        {
            var sys = FindInstance(system, instanceName) ?? throw new InvalidOperationException($"Unknown hardware instance {instanceName}");
            inst = Clone(sys);
            EnsureItem(system, project, inst.TypeName).InstanceList!.Add(inst);
        }
        inst.PinTeam = pins;
    }

    /// <summary>Deletes an instance from the project table, then drops system types left without instances.</summary>
    public static bool RemoveInstance(HardwareTable? system, HardwareTable project, string instanceName)
    {
        var removed = false;
        foreach (var item in project.HardwareItemList ?? [])
        {
            var index = IndexOfInstance(item, instanceName);
            if (index < 0) continue;
            item.InstanceList!.RemoveAt(index);
            removed = true;
            break;
        }
        RemoveSysTypeHasNoInstItem(system, project);
        return removed;
    }

    /// <summary>Deletes a project-defined type with its instances (system types cannot be deleted).</summary>
    public static bool RemoveType(HardwareTable? system, HardwareTable project, string typeName)
    {
        if (ContainsType(system, typeName)) return false;
        var item = FindItem(project, typeName);
        return item != null && project.HardwareItemList!.Remove(item);
    }

    private static HardwareItem EnsureItem(HardwareTable? system, HardwareTable project, string? typeName)
    {
        var item = FindItem(project, typeName);
        if (item != null)
        {
            item.InstanceList ??= new();
            return item;
        }
        var sys = FindItem(system, typeName) ?? throw new InvalidOperationException($"Unknown hardware type {typeName}");
        item = new HardwareItem { HardwareType = Clone(sys.HardwareType!), InstanceList = new() };
        (project.HardwareItemList ??= new()).Add(item);
        return item;
    }

    private static int IndexOfInstance(HardwareItem item, string? name)
    {
        var list = item.InstanceList;
        if (list == null) return -1;
        for (var i = 0; i < list.Count; i++)
            if (string.Equals(list[i].Name, name, StringComparison.Ordinal)) return i;
        return -1;
    }

    public static HardwareTable Clone(HardwareTable t)
    {
        var copy = new HardwareTable { Version = t.Version, Description = t.Description, SoftwareType = t.SoftwareType };
        foreach (var item in t.HardwareItemList ?? []) copy.HardwareItemList!.Add(Clone(item));
        return copy;
    }

    public static HardwareItem Clone(HardwareItem i) => new()
    {
        Version = i.Version,
        HardwareType = i.HardwareType == null ? null : Clone(i.HardwareType),
        InstanceList = i.InstanceList == null ? null : new ArrayList<HardwareInstance>(i.InstanceList.Select(Clone)),
    };

    public static HardwareType Clone(HardwareType t) => new()
    {
        Version = t.Version,
        Name = t.Name,
        PinCount = t.PinCount,
        PinDescriptionList = t.PinDescriptionList == null ? null : new ArrayList<string?>(t.PinDescriptionList),
        IsDefault = t.IsDefault,
        Description = t.Description,
        CtrlLimitList = t.CtrlLimitList == null ? null : new ArrayList<int>(t.CtrlLimitList),
        NickName = t.NickName,
        GNDLimitList = t.GNDLimitList == null ? null : new ArrayList<int>(t.GNDLimitList),
        VccLimitList = t.VccLimitList == null ? null : new ArrayList<int>(t.VccLimitList),
    };

    public static HardwareInstance Clone(HardwareInstance i) => new()
    {
        Version = i.Version,
        TypeName = i.TypeName,
        Name = i.Name,
        PinTeam = i.PinTeam == null ? null : Clone(i.PinTeam),
        TypeNum = i.TypeNum,
    };

    public static PinTeam Clone(PinTeam p) => new()
    {
        CtrlPinNumList = p.CtrlPinNumList == null ? null : new ArrayList<int>(p.CtrlPinNumList),
        GNDPinNumList = p.GNDPinNumList == null ? null : new ArrayList<int>(p.GNDPinNumList),
        VccPinNumList = p.VccPinNumList == null ? null : new ArrayList<int>(p.VccPinNumList),
    };
}

/// <summary>A hardware type used by called subroutines; <see cref="IsDefined"/> false = not in the project's table.</summary>
public sealed record UsedHardwareType(string TypeName, string? NickName, bool IsDefined, List<UsedHardwareInstance> Instances);

/// <summary>A hardware instance used by called subroutines; <see cref="IsDefined"/> false = must still be defined.</summary>
public sealed record UsedHardwareInstance(string Name, bool IsDefined);
