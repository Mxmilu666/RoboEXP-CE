using Avalonia.Controls;
using RoboExp.App.Localization;
using RoboExp.App.Views;
using RoboExp.Core.Hardware;
using RoboExp.Core.Install;
using RoboExp.Core.Model;

namespace RoboExp.App.Hardware;

/// <summary>
/// Entry points of the hardware dialogs. They never modify the project: callers apply the result inside their undo
/// edit (e.g. <see cref="HardwareTables.AddInstance"/>), like the original callers did after <c>DialogResult.OK</c>.
/// </summary>
public static class HardwareDialogs
{
    /// <summary>
    /// HardwareInstFrm (add): define a new instance of <paramref name="hardwareTypeName"/> on the controller picture.
    /// Pins taken by the project's other instances (combined with the system table) are disabled; the name must be
    /// letters/digits and <c>TypeName + text + "_"</c> must be neither an existing instance nor a variable of
    /// <paramref name="data"/>. Returns the new instance (not yet added), or null when cancelled or unavailable
    /// (the type has no controller picture or the hardware type is unknown; a message is shown then).
    /// </summary>
    /// <param name="suggestedInstanceName">Full name to prefill (original instName argument), e.g. an undefined instance a subroutine uses.</param>
    public static async Task<HardwareInstance?> ShowInstanceDialogAsync(Window owner, ProjectData data, RobotType type, VendorInstall install,
        string hardwareTypeName, string? suggestedInstanceName = null)
    {
        var catalog = HardwareCatalog.Get(install, type);
        var table = HardwareTables.GetCombinedTable(catalog.SystemTable, data.HardwareTable);
        var item = HardwareTables.FindItem(table, hardwareTypeName);
        if (catalog.Board == null || item?.HardwareType == null)
        {
            await ShowUnavailableAsync(owner, type);
            return null;
        }
        var pins = new PinAssignment(catalog.Board, item.HardwareType, table);
        var window = new HardwareInstanceWindow(catalog.Board, catalog.Picture, pins, table, data.VarList,
            PinAssignment.InstanceNameText(hardwareTypeName, suggestedInstanceName), isEdit: false);
        await window.ShowDialog(owner);
        if (window.ResultPins == null || window.ResultName == null) return null;
        return new HardwareInstance { TypeName = hardwareTypeName, Name = window.ResultName, PinTeam = window.ResultPins };
    }

    /// <summary>
    /// HardwareInstFrm (edit): re-pick the pins of instance <paramref name="instanceName"/> (its name is fixed).
    /// Returns the new pins (apply with <see cref="HardwareTables.SetInstancePins"/>), or null when cancelled/unavailable.
    /// </summary>
    public static async Task<PinTeam?> ShowEditInstanceDialogAsync(Window owner, ProjectData data, RobotType type, VendorInstall install, string instanceName)
    {
        var catalog = HardwareCatalog.Get(install, type);
        var table = HardwareTables.GetCombinedTable(catalog.SystemTable, data.HardwareTable);
        var inst = HardwareTables.FindInstance(table, instanceName);
        var hwType = HardwareTables.FindItem(table, inst?.TypeName)?.HardwareType;
        if (catalog.Board == null || inst == null || hwType == null)
        {
            await ShowUnavailableAsync(owner, type);
            return null;
        }
        var pins = new PinAssignment(catalog.Board, hwType, table, instanceName, inst.PinTeam);
        var window = new HardwareInstanceWindow(catalog.Board, catalog.Picture, pins, table, data.VarList,
            PinAssignment.InstanceNameText(hwType.Name ?? "", instanceName), isEdit: true);
        await window.ShowDialog(owner);
        return window.ResultPins;
    }

    /// <summary>
    /// HardwareTypeFrm: add a hardware type (<paramref name="editTypeName"/> null) or edit a project-defined one.
    /// Returns the new type / an edited copy (apply by copying into the project's type), or null when cancelled.
    /// </summary>
    public static async Task<HardwareType?> ShowTypeDialogAsync(Window owner, ProjectData data, RobotType type, VendorInstall install, string? editTypeName = null)
    {
        var catalog = HardwareCatalog.Get(install, type);
        var table = HardwareTables.GetCombinedTable(catalog.SystemTable, data.HardwareTable);
        HardwareType? editing = null;
        if (editTypeName != null)
        {
            editing = HardwareTables.FindItem(table, editTypeName)?.HardwareType;
            if (editing == null) return null;
        }
        var window = new HardwareTypeWindow(table, editing, catalog.Board, catalog.Picture);
        await window.ShowDialog(owner);
        return window.Result;
    }

    private static Task ShowUnavailableAsync(Window owner, RobotType type) =>
        MessageDialog.ShowAsync(owner, string.Format(Localizer.Instance.T("Port:Hw.NoBoard"), type.Name),
            Localizer.Instance.T("HardwareInstFrm:$this.Text"), MessageDialog.Buttons.Ok);
}
