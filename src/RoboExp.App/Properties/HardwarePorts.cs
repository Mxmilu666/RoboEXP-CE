using RoboExp.Core.Model;

namespace RoboExp.App.Properties;

/// <summary>
/// Candidate values of hardware/enum parameters (the <c>ComboBoxDisable</c> lists), as hard-coded per controller by
/// PropertyFrmNew.GetHardwareIntance (popup editor) and PropertyFrm.GetHardwareIntance (docked editor).
/// </summary>
public static class HardwarePorts
{
    /// <summary>Marker item that opens the hardware instance dialog (original XWBRM "DefineHardwareInfo").</summary>
    public const string DefineHardwareInfoKey = "Xwb:DefineHardwareInfo";

    private static string[] Seq(char prefix, int n) => Enumerable.Range(1, n).Select(i => $"_{prefix}{i}_").ToArray();

    private static string[] Pick(string type, string[] motors, string[] ports) =>
        type == "_MOTOR_" ? motors : type == "_PORT_" ? ports : [];

    /// <summary>
    /// Candidates for <paramref name="param"/> of function <paramref name="functionName"/>. Returns null when the
    /// original falls back to the project's hardware table (instances of the type + DefineHardwareInfo).
    /// </summary>
    public static string[]? FixedCandidates(SoftwareType software, string? functionName, ParameterClass param, bool docked)
    {
        var type = param.IsHardwareType ? param.Type ?? "" : null;
        return docked ? Docked(software, functionName, type) : Popup(software, functionName, type);
    }

    /// <summary>PropertyFrmNew.GetHardwareIntance → GetHardwareIntanceE2RCU (every hardware param; type known).</summary>
    private static string[]? Popup(SoftwareType software, string? functionName, string? type)
    {
        if (type == null) return null;
        switch (software)
        {
            case SoftwareType.C1RCU:
                if (functionName is "GetUltrasound" or "GetShortIRDist") return ["_IIC1_", "_IIC2_"];
                return Pick(type, Seq('M', 2), Seq('A', 8));
            case SoftwareType.M6RCU: return Pick(type, Seq('M', 4), Seq('P', 5));
            case SoftwareType.C6RCU: return Pick(type, Seq('M', 3), Seq('P', 4));
            case SoftwareType.E7RCU: return Pick(type, Seq('M', 8), Seq('P', 8));
            case SoftwareType.IQ2Robot or SoftwareType.IQrobot: return Pick(type, Seq('M', 4), ["_P1_", "_P2_", "_PL_", "_PM_", "_PR_"]);
            default: return Pick(type, Seq('M', 4), Seq('P', 8));
        }
    }

    /// <summary>PropertyFrm.GetHardwareIntance (docked editor) including its quirks (E7/M7/M6/C6 ignore a null type).</summary>
    private static string[]? Docked(SoftwareType software, string? functionName, string? type)
    {
        if (type != null && software is SoftwareType.E2RCU or SoftwareType.E3RCU or SoftwareType.E6RCU or SoftwareType.EV5)
            return Pick(type, Seq('M', 4), Seq('P', 8));
        switch (software)
        {
            case SoftwareType.E7RCU or SoftwareType.M7RCU: return type == null ? [] : Pick(type, Seq('M', 8), Seq('P', 8));
            case SoftwareType.M6RCU: return type == null ? [] : Pick(type, Seq('M', 4), Seq('P', 5));
            case SoftwareType.C6RCU: return type == null ? [] : Pick(type, Seq('M', 3), Seq('P', 4));
        }
        if (type == null) return null;
        switch (software)
        {
            case SoftwareType.C1RCU:
                if (functionName == "Set3CLed") return ["_RGB1_", "_RGB2_", "_RGB3_", "_RGB4_", "_RGB5_"];
                if (functionName == "SetMotorServo") return ["_S1_", "_S2_"];
                if (functionName is "GetUltrasound" or "GetShortIRDist") return ["_IIC1_", "_IIC2_"];
                return Pick(type, Seq('M', 2), Seq('A', 8));
            case SoftwareType.MRCU: return Pick(type, Seq('M', 2), Seq('P', 4));
            case SoftwareType.M2RCU: return Pick(type, Seq('M', 3), Seq('P', 4));
            case SoftwareType.IQrobot or SoftwareType.IQ2Robot: return Pick(type, Seq('M', 4), ["_P1_", "_P2_", "_PL_", "_PM_", "_PR_"]);
            default: return null;
        }
    }

    /// <summary>Fallback list: the hardware table's instances of the parameter type (by type name), without the DefineHardwareInfo marker.</summary>
    public static IReadOnlyList<string> TableInstances(HardwareTable? table, ParameterClass param)
    {
        if (!param.IsHardwareType || table?.HardwareItemList == null) return [];
        var item = table.HardwareItemList.FirstOrDefault(i => i.HardwareType?.Name == param.Type);
        return item?.InstanceList?.Select(i => i.Name ?? "").ToList() ?? [];
    }
}
