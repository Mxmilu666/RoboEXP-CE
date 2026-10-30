using RoboExp.App.Properties;
using RoboExp.Core.Library;
using RoboExp.Core.Model;
using Xunit;

namespace RoboExp.Tests.App.Properties;

public class SpecialPanelsTests
{
    [Theory]
    [InlineData("SetMotor", SpecialPanelKind.Motor)]
    [InlineData("SetFan", SpecialPanelKind.Motor)]
    [InlineData("Set3CLed", SpecialPanelKind.Led)]
    [InlineData("SetDisplayFloat", SpecialPanelKind.Display)]
    [InlineData("GetUltrasound", SpecialPanelKind.PortGetter)]
    [InlineData("SetWaitforLighter", SpecialPanelKind.WaitLight)]
    [InlineData("SetMotorCode", SpecialPanelKind.WaitTouch)]
    [InlineData("SetLCDBack", SpecialPanelKind.OnOff)]
    [InlineData("SetWaitForTime", SpecialPanelKind.WaitTime)]
    [InlineData("SetBluetooth", SpecialPanelKind.Threshold)]
    [InlineData("GetWifiRemote", SpecialPanelKind.Getter)]
    [InlineData("SetLCDDot", SpecialPanelKind.LcdDot)]
    [InlineData("GetChoiceBox", SpecialPanelKind.ChoiceBox)]
    [InlineData("GetData", SpecialPanelKind.Data)]
    [InlineData("SetLCDClear", SpecialPanelKind.LcdClear)]
    [InlineData("SetMotorFree", SpecialPanelKind.None)]
    [InlineData("MyUserSub", SpecialPanelKind.None)]
    public void Dispatch_by_function_name(string fn, SpecialPanelKind kind) => Assert.Equal(kind, SpecialPanels.ForFunction(fn));

    [Fact]
    public void Motor_port_lists_follow_the_original_override_order()
    {
        Assert.Equal(["", "_M1_", "_M2_", "_M3_", "_M4_"], SpecialPanels.MotorPorts(SoftwareType.E6RCU, "SetMotor"));
        Assert.Equal(["", "_M1_", "_M2_", "_M3_"], SpecialPanels.MotorPorts(SoftwareType.C6RCU, "SetMotorServo"));
        // C1 SetMotorServo: the popup _MOTOR_ list ("SetMotor" substring) wins over the _S1_/_S2_ list, like the original.
        Assert.Equal(["", "_M1_", "_M2_"], SpecialPanels.MotorPorts(SoftwareType.C1RCU, "SetMotorServo"));
        Assert.Equal(["_M1_", "_M2_"], SpecialPanels.MotorPorts(SoftwareType.C1RCU, "SetTurnleft"));
        Assert.Equal(["", "_P1_", "_P2_", "_P3_", "_P4_", "_P5_"], SpecialPanels.Ports(SoftwareType.M6RCU, "GetLightSensor"));
        Assert.Equal(["", "_M1_", "_M2_", "_M3_", "_M4_", "_M5_", "_M6_", "_M7_", "_M8_"], SpecialPanels.Ports(SoftwareType.E7RCU, "GetMotorCode"));
    }

    [Fact]
    public void Led_mapping()
    {
        Assert.Equal(3, SpecialPanels.LedPortIndex("_P3_"));
        Assert.Equal(5, SpecialPanels.LedPortIndex("_RGB5_"));
        Assert.Equal(-1, SpecialPanels.LedPortIndex("_RGB6_"));
        Assert.Equal("BLUE", SpecialPanels.LedColorOf("3"));
        Assert.Equal("WHITE", SpecialPanels.LedColorOf("7"));
        Assert.Equal("CYAN", SpecialPanels.LedColorOf("CYAN"));
        Assert.Null(SpecialPanels.LedColorOf("8"));
        Assert.Equal(" ", SpecialPanels.LedPorts(SoftwareType.C1RCU)[0]);
    }

    [Fact]
    public void Ranges_come_from_code_func_parameters_only()
    {
        var lib = new ProjectInfo { ProjectType = ProjectType.CodeFunc };
        lib.ParamList!.Add(new ParameterClass { Name = "speed", Min = "0", Max = "100" });
        lib.ParamList.Add(new ParameterClass { Name = "angle", Min = "0", Max = "220" });
        lib.ParamList.Add(new ParameterClass { Name = "value", Min = "0", Max = "1023" });
        Assert.Equal((0, 100), SpecialPanels.SpeedRange(lib));
        Assert.Equal((0, 180), SpecialPanels.AngleRange(lib, SoftwareType.C1RCU, "SetMotorServo"));
        Assert.Equal((0, 220), SpecialPanels.AngleRange(lib, SoftwareType.E6RCU, "SetMotorServo"));
        Assert.Equal((0, 1023), SpecialPanels.ValueRange(lib));
        var flow = new ProjectInfo { ProjectType = ProjectType.FlowFunc, ParamList = lib.ParamList };
        Assert.Equal((-100, 100), SpecialPanels.SpeedRange(flow)); // designer default unless a CodeFunc
        Assert.Equal((0, 1023), SpecialPanels.ValueRange(flow));   // value ranges apply to any callee
        Assert.Equal((0, 4095), SpecialPanels.ValueRange(null));
    }

    [Fact]
    public void Text_rules()
    {
        Assert.Equal("1", SpecialPanels.LoopCountFor(""));
        Assert.Equal("25", SpecialPanels.LoopCountFor("25"));
        Assert.Null(SpecialPanels.LoopCountFor("2a"));
        Assert.True(SpecialPanels.QuotesText("GetChoiceBox"));
        Assert.False(SpecialPanels.QuotesText("SetDisplayFloat"));
        Assert.True(SpecialPanels.IsInRange("-100", -100, 100));
        Assert.False(SpecialPanels.IsInRange("101", -100, 100));
        Assert.Equal("界 线 值", SpecialPanels.ThresholdCaption("SetWaitforDark", UiLanguage.CHS));
        Assert.Equal("范 圍 值", SpecialPanels.ThresholdCaption("SetWaitforLouder", UiLanguage.CHT));
        Assert.Null(SpecialPanels.ThresholdCaption("SetBluetooth", UiLanguage.Default));
        Assert.Equal(["1", "2", "3", "4"], SpecialPanels.DisplayLines(SoftwareType.M2RCU));
        Assert.Equal((true, false, false, false, true), SpecialPanels.DisplayRows(SoftwareType.MRCU, "SetDisplayVarBW"));
    }

    [Fact]
    public void Port_picker_styles()
    {
        Assert.Null(SpecialPanels.PortStyle(SoftwareType.E6RCU, "SetMotor", SpecialPanels.PortPicker.Motor));
        Assert.Equal(IqPortStyle.PortM1M2, SpecialPanels.PortStyle(SoftwareType.MRCU, "SetMotor", SpecialPanels.PortPicker.Motor));
        Assert.Equal(IqPortStyle.PortM1M2M3, SpecialPanels.PortStyle(SoftwareType.M2RCU, "SetMotor", SpecialPanels.PortPicker.Motor));
        Assert.Equal(IqPortStyle.PortNone, SpecialPanels.PortStyle(SoftwareType.IQ2Robot, "SetForward", SpecialPanels.PortPicker.Motor));
        Assert.Equal(IqPortStyle.PortM3M4, SpecialPanels.PortStyle(SoftwareType.IQ2Robot, "SetFan", SpecialPanels.PortPicker.Motor));
        Assert.Equal(IqPortStyle.PortLMRP1P2, SpecialPanels.PortStyle(SoftwareType.IQ2Robot, "GetLightSensor", SpecialPanels.PortPicker.Def));
        Assert.Equal(IqPortStyle.PortLRP1P2, SpecialPanels.PortStyle(SoftwareType.IQ2Robot, "GetTouch", SpecialPanels.PortPicker.Def));
        Assert.Equal(IqPortStyle.PortP1P2P3P4, SpecialPanels.PortStyle(SoftwareType.MRCU, "GetVolume", SpecialPanels.PortPicker.Def));
        Assert.Equal([("L", "_M1_"), ("R", "_M2_"), ("M3", "_M3_"), ("M4", "_M4_")], SpecialPanels.PortChoices(IqPortStyle.PortLRM3M4));
        Assert.Equal(20, SpecialPanels.RfRemoteButtons.Count);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => i.ToString()).Order(), SpecialPanels.RfRemoteButtons.Select(b => b.Value).Order());
    }
}
