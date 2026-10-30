using System.Globalization;
using RoboExp.Core.Library;
using RoboExp.Core.Model;

namespace RoboExp.App.Properties;

/// <summary>
/// The panels of PropertyFrmNew.SetTypeVisible(n) (values are the original n). 0..3 (grid, expression, code editor,
/// default) are the generic editors; 4..17 are chosen by function name in SetProperty; 18 is the For panel.
/// </summary>
public enum SpecialPanelKind
{
    None = 0,
    Motor = 4,          // PnlMotor
    Led = 5,            // Pnl_Led (Set3CLed)
    Display = 6,        // PnlLCD8
    PortGetter = 7,     // PnlDeF: port + return variable (+ RF remote buttons)
    WaitLight = 8,      // PnlDeF: port + threshold value
    WaitTouch = 9,      // PnlDeF: port only
    OnOff = 10,         // PnlDeF: OFF/ON (+ port for SetLightSensorLed)
    WaitTime = 11,      // PnlDeF: seconds
    Threshold = 12,     // PnlDeF: value without port
    Getter = 13,        // PnlDeF: return variable only (+ WiFi remote buttons)
    LcdDot = 14,        // Pln_LCDPonit
    ChoiceBox = 15,     // Pnl_varReturn
    Data = 16,          // Pnl_SetSave
    LcdClear = 17,      // Pnl_LDCBack
    For = 18,           // Pnl_ForData
}

/// <summary>IQRobotPort.PortStyleEnum: radio-button port pickers shown for IQrobot/IQ2Robot/MRCU/M2RCU.</summary>
public enum IqPortStyle { PortLMRP1P2, PortLRP1P2, PortLRM3M4, PortP1P2, PortM3M4, PortNone, PortP1P2P3P4, PortM1M2, PortM1M2M3 }

/// <summary>Pure rules of PropertyFrmNew's per-function panels (SetProperty 1059-1126, SetTypeVisible, IQRobotPort).</summary>
public static class SpecialPanels
{
    private static readonly string[] MotorFns = ["SetMotor", "SetMotorServo", "SetWaitForAngle", "SetTurnleft", "SetTurnright", "SetMotorServo128", "SetWaitForAngle128", "SetForward", "SetBackward", "SetFan"];
    private static readonly string[] DisplayFns = ["SetDisplayVar", "SetDisplayVarBW", "SetDisplayString", "GetWifiVoice", "SetWifiVoiceString", "SetDisplayFloat"];
    private static readonly string[] PortGetterFns =
    [
        "GetLightSensor", "GetMotorCode", "GetTouch", "GetRFModuleRemoteBit", "GetNumberOfClicks", "GetIRone", "GetIRDist", "GetInfrared", "GetPhoto",
        "GetCompassB", "GetVolume", "GetVoltage", "GetCurrent", "GetMagnet", "GetAirQuality", "GetRemoIR", "GetAntiLight", "GetShortIRDist",
        "GetUltrasound", "GetLightSensorData", "GetLightSensorThreshold",
    ];
    private static readonly string[] WaitLightFns = ["SetWaitforDark", "SetWaitforLight", "SetWaitforDarker", "SetWaitforLighter"];
    private static readonly string[] WaitTouchFns = ["SetWaitForPress", "SetWaitForBump", "SetWaitForRelease", "SetMotorCode"];
    private static readonly string[] OnOffFns = ["SetLightSensorLed", "SetInBeep", "SetLCDBack"];
    private static readonly string[] ThresholdFns = ["SetWaitforLoud", "SetWaitforQuiet", "SetWaitforQuieter", "SetWaitforLouder", "SetInWireless", "SetLightSensorThreshold", "SetBluetooth"];
    private static readonly string[] GetterFns =
    [
        "GetRightButton", "GetLeftButton", "GetInMic", "GetInWireless", "GetInVotage", "GetBluetooth", "GetChoiceBox1", "GetSysTime", "GetTouchScreen",
        "GetTouchScreenX", "GetTouchScreenY", "GetWifiRemote", "GetIRCode", "GetBluetoothControlData", "GetBluetoothData", "GetBluetoothControl",
    ];
    private static readonly string[] AngleFns = ["SetMotorServo", "SetWaitForAngle", "SetTurnright", "SetTurnleft", "SetMotorServo128", "SetBackward", "SetForward", "SetWaitForAngle128"];

    /// <summary>SetProperty's dispatch on the called function's name (the else branch = the generic grid).</summary>
    public static SpecialPanelKind ForFunction(string? name)
    {
        if (string.IsNullOrEmpty(name)) return SpecialPanelKind.None;
        if (MotorFns.Contains(name)) return SpecialPanelKind.Motor;
        if (name == "Set3CLed") return SpecialPanelKind.Led;
        if (DisplayFns.Contains(name)) return SpecialPanelKind.Display;
        if (PortGetterFns.Contains(name)) return SpecialPanelKind.PortGetter;
        if (WaitLightFns.Contains(name)) return SpecialPanelKind.WaitLight;
        if (WaitTouchFns.Contains(name)) return SpecialPanelKind.WaitTouch;
        if (OnOffFns.Contains(name)) return SpecialPanelKind.OnOff;
        if (name == "SetWaitForTime") return SpecialPanelKind.WaitTime;
        if (ThresholdFns.Contains(name)) return SpecialPanelKind.Threshold;
        if (GetterFns.Contains(name)) return SpecialPanelKind.Getter;
        if (name == "SetLCDDot") return SpecialPanelKind.LcdDot;
        if (name == "GetChoiceBox") return SpecialPanelKind.ChoiceBox;
        if (name is "SetData" or "GetData") return SpecialPanelKind.Data;
        if (name == "SetLCDClear") return SpecialPanelKind.LcdClear;
        return SpecialPanelKind.None;
    }

    /// <summary>Case 4: the angle row (label17/comboBox_Angle/hScrollBar2) exists for servo/turn functions.</summary>
    public static bool ShowsAngle(string fn) => AngleFns.Contains(fn);

    /// <summary>C1RCU SetMotorServo hides the speed row (servo port has no speed).</summary>
    public static bool ShowsSpeed(SoftwareType t, string fn) => !(t == SoftwareType.C1RCU && fn == "SetMotorServo");

    private static string[] PortList(SoftwareType t, string fn, string type) =>
        HardwarePorts.FixedCandidates(t, fn, new ParameterClass { Type = type, IsHardwareType = true }, docked: false) ?? [];

    /// <summary>
    /// comboBox_Motorwhich items: designer ["", _M1_.._M4_]; C1 [_S1_,_S2_] (SetMotorServo) or [_M1_,_M2_]; C6 ["",_M1_.._M3_];
    /// then GetMotorCode / *SetMotor* / *SetWaitForAngle* use "" + the popup _MOTOR_ list (which wins).
    /// </summary>
    public static IReadOnlyList<string> MotorPorts(SoftwareType t, string fn)
    {
        if (fn == "GetMotorCode" || fn.Contains("SetMotor", StringComparison.Ordinal) || fn.Contains("SetWaitForAngle", StringComparison.Ordinal))
            return ["", .. PortList(t, fn, "_MOTOR_")];
        if (t == SoftwareType.C6RCU) return ["", "_M1_", "_M2_", "_M3_"];
        if (t == SoftwareType.C1RCU) return fn == "SetMotorServo" ? ["_S1_", "_S2_"] : ["_M1_", "_M2_"];
        return ["", "_M1_", "_M2_", "_M3_", "_M4_"];
    }

    /// <summary>comboBox_Def items: "" + the popup list of _MOTOR_ (GetMotorCode/SetMotor*/SetWaitForAngle*) or _PORT_.</summary>
    public static IReadOnlyList<string> Ports(SoftwareType t, string fn)
    {
        var motor = fn == "GetMotorCode" || fn.Contains("SetMotor", StringComparison.Ordinal) || fn.Contains("SetWaitForAngle", StringComparison.Ordinal);
        return ["", .. PortList(t, fn, motor ? "_MOTOR_" : "_PORT_")];
    }

    /// <summary>CB_LED items (index 0 = blank, written as " " like the original).</summary>
    public static IReadOnlyList<string> LedPorts(SoftwareType t) => t switch
    {
        SoftwareType.C1RCU => [" ", "_RGB1_", "_RGB2_", "_RGB3_", "_RGB4_", "_RGB5_"],
        SoftwareType.M6RCU => [" ", "_P1_", "_P2_", "_P3_", "_P4_", "_P5_"],
        SoftwareType.C6RCU => [" ", "_P1_", "_P2_", "_P3_", "_P4_"],
        _ => ["", "_P1_", "_P2_", "_P3_", "_P4_", "_P5_", "_P6_", "_P7_", "_P8_"],
    };

    /// <summary>Case 5 "which" → CB_LED.SelectedIndex (_RGBn_ / _Pn_ → n), -1 when unknown.</summary>
    public static int LedPortIndex(string? value)
    {
        if (value is null) return -1;
        foreach (var prefix in new[] { "_RGB", "_P" })
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal) && value.EndsWith('_')
                && int.TryParse(value[prefix.Length..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                && n >= 1 && n <= (prefix == "_RGB" ? 5 : 8))
                return n;
        }
        return -1;
    }

    /// <summary>Set3CLed colour radios in order (radioButton1..7,10; Tag written as the Color value).</summary>
    public static IReadOnlyList<string> LedColors { get; } = ["OFF", "RED", "GREEN", "BLUE", "YELLOW", "PURPLE", "CYAN", "WHITE"];

    /// <summary>Case 5: "0".."7" or a colour name selects a radio; null when nothing matches.</summary>
    public static string? LedColorOf(string? value)
    {
        if (value is { Length: 1 } && value[0] is >= '0' and <= '7') return LedColors[value[0] - '0'];
        return value != null && LedColors.Contains(value) ? value : null;
    }

    /// <summary>comboBox_FColor/BGColor/LCDBack items.</summary>
    public static IReadOnlyList<string> DisplayColors { get; } = ["BLACK", "RED", "GREEN", "BLUE", "YELLOW", "PURPLE", "CYAN", "WHITE"];

    /// <summary>comboBox3 (SetLCDDot Color) items: a blank entry then the colours.</summary>
    public static IReadOnlyList<string> DotColors { get; } = ["", .. DisplayColors];

    /// <summary>comboBox_y items: 1..19; MRCU/M2RCU trim to 1..4 when the list drops down.</summary>
    public static IReadOnlyList<string> DisplayLines(SoftwareType t) =>
        t is SoftwareType.MRCU or SoftwareType.M2RCU
            ? ["1", "2", "3", "4"]
            : Enumerable.Range(1, 19).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();

    /// <summary>
    /// Case 6 row visibility per function and controller: (data list, free text, return variable, colours).
    /// </summary>
    public static (bool DataList, bool DataText, bool ReturnVar, bool Colors, bool Line) DisplayRows(SoftwareType t, string fn)
    {
        var mono = t is SoftwareType.MRCU or SoftwareType.M2RCU;
        return fn switch
        {
            "SetDisplayVar" or "SetDisplayVarBW" => (true, false, false, !mono, true),
            "SetDisplayString" or "SetDisplayFloat" => (false, true, false, !mono, true),
            "GetWifiVoice" => (false, true, true, false, false),
            _ => (false, false, false, true, true),
        };
    }

    /// <summary>comboBox_TextChangedNoCheck: these functions store the text quoted.</summary>
    public static bool QuotesText(string fn) => fn is "SetDisplayString" or "GetChoiceBox" or "GetWifiVoice";

    /// <summary>The 30-character limit of comboBox_TextChangedNoCheck.</summary>
    public const int MaxTextLength = 30;

    /// <summary>comboBox_ForData: at most 8 characters.</summary>
    public const int MaxLoopTextLength = 8;

    /// <summary>label22 caption for cases 8/12 ("界 线 值" / "范 围 值" / "Value"); null keeps the designer text.</summary>
    public static string? ThresholdCaption(string fn, UiLanguage lang)
    {
        if (fn is "SetWaitforLight" or "SetWaitforDark" or "SetWaitforLoud" or "SetWaitforQuiet")
            return lang switch { UiLanguage.CHS => "界 线 值", UiLanguage.CHT => "界 線 值", _ => "Value" };
        if (fn is "SetWaitforQuieter" or "SetWaitforLouder" or "SetWaitforDarker" or "SetWaitforLighter")
            return lang switch { UiLanguage.CHS => "范 围 值", UiLanguage.CHT => "范 圍 值", _ => "Value" };
        return null;
    }

    /// <summary>
    /// Scroll bar ranges (SetTypeVisible): speed -100..100 (designer), from the "speed" param of a library function when
    /// it has a Max; angle 1..3600 or the "angle" param's Min..Max (C1 SetMotorServo caps at 180); value 0..4095 or the
    /// "value" param's Min..Max.
    /// </summary>
    public static (int Min, int Max) SpeedRange(ProjectInfo? callee) => RangeOf(callee, "speed", (-100, 100), requireCodeFunc: true);

    public static (int Min, int Max) AngleRange(ProjectInfo? callee, SoftwareType t, string fn)
    {
        var r = RangeOf(callee, "angle", (1, 3600), requireCodeFunc: true);
        return t == SoftwareType.C1RCU && fn == "SetMotorServo" ? (r.Min, 180) : r;
    }

    public static (int Min, int Max) ValueRange(ProjectInfo? callee) => RangeOf(callee, "value", (0, 4095), requireCodeFunc: false);

    private static (int Min, int Max) RangeOf(ProjectInfo? callee, string name, (int, int) fallback, bool requireCodeFunc)
    {
        var p = callee?.ParamList?.FirstOrDefault(x => x.Name == name);
        if (p == null || string.IsNullOrWhiteSpace(p.Max) || (requireCodeFunc && callee!.ProjectType != ProjectType.CodeFunc)) return fallback;
        if (!int.TryParse(p.Max, NumberStyles.Integer, CultureInfo.InvariantCulture, out var max)) return fallback;
        var min = int.TryParse(p.Min, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 0;
        return (min, max);
    }

    /// <summary>isnewNumber + range: the text is an integer inside [min, max] (the scroll bar stays visible).</summary>
    public static bool IsInRange(string text, int min, int max) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max;

    /// <summary>
    /// comboBox_ForData_TextChanged: digits only update LoopCountStr ("" → "1"); returns null when the text is not a number
    /// (the original leaves the data unchanged).
    /// </summary>
    public static string? LoopCountFor(string text)
    {
        if (text.Length == 0) return "1";
        var body = text[0] == '-' ? text[1..] : text;
        return body.Length > 0 && body.All(char.IsAsciiDigit) ? text : null;
    }

    /// <summary>The IQRobotPort control shown by panel 7..12 (iqRobotPort3), the motor panel (2) and the LED panel (1).</summary>
    public enum PortPicker { Led = 1, Motor = 2, Def = 3 }

    /// <summary>Whether the controller uses the radio port pickers (HideAll brings them to front).</summary>
    public static bool UsesPortPicker(SoftwareType t) => t is SoftwareType.IQrobot or SoftwareType.IQ2Robot or SoftwareType.MRCU or SoftwareType.M2RCU;

    private static readonly string[] LightGroup = ["SetWaitforDark", "SetWaitforLight", "SetWaitforLighter", "GetLightSensor", "SetWaitforDarker", "GetIRone"];
    private static readonly string[] TouchGroup = ["GetTouch", "SetWaitForPress", "SetWaitForRelease", "SetWaitForBump", "GetRemoIR"];
    private static readonly string[] SensorGroup = ["SetLightSensorLed", "GetAntiLight", "SetIRone", "GetPhoto", "GetInfrared", "GetIRDist", "GetCompassB", "GetVolume", "GetVoltage", "GetCurrent", "GetMagnet", "GetAirQuality"];
    private static readonly string[] TurnGroup = ["SetTurnleft", "SetTurnright", "SetForward", "SetBackward"];

    /// <summary>SetProperty's PortStyle assignment; null = that picker is not configured for the function (the port combobox is used).</summary>
    public static IqPortStyle? PortStyle(SoftwareType t, string fn, PortPicker picker)
    {
        if (!UsesPortPicker(t)) return null;
        var iq = t is SoftwareType.IQrobot or SoftwareType.IQ2Robot;
        switch (picker)
        {
            case PortPicker.Motor:
                if (TurnGroup.Contains(fn)) return IqPortStyle.PortNone;
                if (iq) return fn == "SetFan" ? IqPortStyle.PortM3M4 : IqPortStyle.PortLRM3M4;
                return t == SoftwareType.MRCU ? IqPortStyle.PortM1M2 : IqPortStyle.PortM1M2M3;
            case PortPicker.Led:
                if (iq) return IqPortStyle.PortLMRP1P2; // never configured: the control's initial style
                return LightGroup.Contains(fn) || fn == "Set3CLed" ? IqPortStyle.PortP1P2P3P4 : null;
            default:
                if (fn is "GetMotorCode" or "SetMotorCode") return iq ? IqPortStyle.PortLRM3M4 : t == SoftwareType.MRCU ? IqPortStyle.PortM1M2 : IqPortStyle.PortM1M2M3;
                if (!iq && t == SoftwareType.M2RCU && fn == "SetMotor") return IqPortStyle.PortM1M2M3;
                if (LightGroup.Contains(fn) || (!iq && fn == "Set3CLed")) return iq ? IqPortStyle.PortLMRP1P2 : IqPortStyle.PortP1P2P3P4;
                if (TouchGroup.Contains(fn)) return iq ? IqPortStyle.PortLRP1P2 : IqPortStyle.PortP1P2P3P4;
                if (SensorGroup.Contains(fn)) return iq ? IqPortStyle.PortP1P2 : IqPortStyle.PortP1P2P3P4;
                return null;
        }
    }

    /// <summary>IQRobotPort.PortStyle setter: (caption, port value) of the visible radio buttons, in control order.</summary>
    public static IReadOnlyList<(string Caption, string Value)> PortChoices(IqPortStyle style) => style switch
    {
        IqPortStyle.PortLMRP1P2 => [("L", "_PL_"), ("M", "_PM_"), ("R", "_PR_"), ("P1", "_P1_"), ("P2", "_P2_")],
        IqPortStyle.PortLRP1P2 => [("L", "_PL_"), ("R", "_PR_"), ("P1", "_P1_"), ("P2", "_P2_")],
        IqPortStyle.PortLRM3M4 => [("L", "_M1_"), ("R", "_M2_"), ("M3", "_M3_"), ("M4", "_M4_")],
        IqPortStyle.PortP1P2 => [("P1", "_P1_"), ("P2", "_P2_")],
        IqPortStyle.PortM3M4 => [("M3", "_M3_"), ("M4", "_M4_")],
        IqPortStyle.PortM1M2 => [("M1", "_M1_"), ("M2", "_M2_")],
        IqPortStyle.PortP1P2P3P4 => [("P1", "_P1_"), ("P2", "_P2_"), ("P3", "_P3_"), ("P4", "_P4_")],
        IqPortStyle.PortM1M2M3 => [("M1", "_M1_"), ("M2", "_M2_"), ("M3", "_M3_")],
        _ => [],
    };

    /// <summary>GetRFModuleRemoteBit buttons (radioButtonEx1..20 TextW 0..19) at their positions on pictureBox3.</summary>
    public static IReadOnlyList<(string Value, int X, int Y)> RfRemoteButtons { get; } =
    [
        ("4", 142, 70), ("1", 158, 53), ("3", 158, 86), ("5", 33, 55), ("6", 47, 70), ("19", 46, 113), ("7", 33, 87), ("18", 62, 129),
        ("8", 17, 70), ("17", 75, 113), ("9", 32, 26), ("16", 60, 98), ("10", 32, 12), ("15", 118, 114), ("0", 158, 26), ("14", 134, 129),
        ("11", 158, 12), ("13", 146, 113), ("12", 132, 98), ("2", 173, 69),
    ];

    /// <summary>GetWifiRemote buttons (radioButtonEx21..32 TextW 1..12) on pictureBox4.</summary>
    public static IReadOnlyList<(string Value, int X, int Y)> WifiRemoteButtons { get; } =
    [
        ("12", 123, 10), ("11", 106, 10), ("10", 88, 10), ("9", 71, 10), ("8", 30, 53), ("7", 49, 72), ("6", 67, 53), ("5", 49, 34),
        ("4", 127, 53), ("3", 147, 74), ("2", 167, 54), ("1", 146, 35),
    ];
}
