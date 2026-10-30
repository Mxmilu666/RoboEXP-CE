using RoboExp.App.NewProject;
using RoboExp.App.Properties;
using RoboExp.App.Variables;
using RoboExp.Core.CodeGen;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;
using Xunit;

namespace RoboExp.Tests.App.Properties;

public class ValueCheckTests
{
    [Theory]
    [InlineData("127", "char", true)]
    [InlineData("128", "char", false)]
    [InlineData("'a'", "char", true)]
    [InlineData("255", "unsigned char", true)]
    [InlineData("-1", "unsigned char", false)]
    [InlineData("\"hi\"", "string", true)]
    [InlineData("2147483647", "long", true)]
    [InlineData("2147483648", "long", false)]
    [InlineData("1.5", "double", true)]
    [InlineData("abc", "double", false)]
    [InlineData("", "long", false)]
    [InlineData("1", "float", false)]
    public void IsValueOfType(string v, string type, bool expected) =>
        Assert.Equal(expected, ValueCheck.IsValueOfType(v, type, SoftwareType.E6RCU));

    [Fact]
    public void IntRangeDependsOnController()
    {
        Assert.False(ValueCheck.IsValueOfType("40000", "int", SoftwareType.E6RCU));
        Assert.True(ValueCheck.IsValueOfType("40000", "int", SoftwareType.X4RCU));
        Assert.False(ValueCheck.IsValueOfType("70000", "unsigned int", SoftwareType.M6RCU));
        Assert.True(ValueCheck.IsValueOfType("70000", "unsigned int", SoftwareType.E2RCU));
    }

    [Fact]
    public void IsMoreQuirks()
    {
        Assert.True(ValueCheck.IsMore("10", "9"));
        Assert.False(ValueCheck.IsMore("9", "10"));
        Assert.True(ValueCheck.IsMore("'b'", "'a'"));
        Assert.True(ValueCheck.IsMore("x", "10")); // unparsable → true like the original
    }

    [Theory]
    [InlineData("var1", true)]
    [InlineData("_x", true)]
    [InlineData("1a", false)]
    [InlineData("a-b", false)]
    [InlineData("", false)]
    [InlineData("名字", false)]
    public void RightVarName(string s, bool ok) => Assert.Equal(ok, ValueCheck.IsRightVarName(s));

    private static ParameterClass Speed => new() { Name = "speed", Type = "long", Min = "-100", Max = "100" };

    [Fact]
    public void CheckParameterRangesAndNames()
    {
        bool Known(string n) => n == "var1";
        Assert.Equal(ValueCheck.ParamError.None, ValueCheck.CheckParameter(Speed, "50", Known, SoftwareType.E6RCU));
        Assert.Equal(ValueCheck.ParamError.None, ValueCheck.CheckParameter(Speed, "var1", Known, SoftwareType.E6RCU));
        Assert.Equal(ValueCheck.ParamError.None, ValueCheck.CheckParameter(Speed, "", Known, SoftwareType.E6RCU));
        Assert.Equal(ValueCheck.ParamError.MoreThanMax, ValueCheck.CheckParameter(Speed, "101", Known, SoftwareType.E6RCU));
        Assert.Equal(ValueCheck.ParamError.LessThanMin, ValueCheck.CheckParameter(Speed, "-101", Known, SoftwareType.E6RCU));
        Assert.Equal(ValueCheck.ParamError.DataType, ValueCheck.CheckParameter(Speed, "1.5", Known, SoftwareType.E6RCU));
        Assert.Equal(ValueCheck.ParamError.DataType, ValueCheck.CheckParameter(Speed, "unknownVar", Known, SoftwareType.E6RCU));
    }
}

public class HardwarePortsTests
{
    private static ParameterClass Hw(string type) => new() { Name = "p", Type = type, IsHardwareType = true };

    [Theory]
    [InlineData(SoftwareType.E6RCU, "_MOTOR_", 4, "_M4_")]
    [InlineData(SoftwareType.E6RCU, "_PORT_", 8, "_P8_")]
    [InlineData(SoftwareType.M6RCU, "_PORT_", 5, "_P5_")]
    [InlineData(SoftwareType.C6RCU, "_MOTOR_", 3, "_M3_")]
    [InlineData(SoftwareType.E7RCU, "_MOTOR_", 8, "_M8_")]
    [InlineData(SoftwareType.C1RCU, "_PORT_", 8, "_A8_")]
    [InlineData(SoftwareType.IQ2Robot, "_PORT_", 5, "_PR_")]
    public void PopupLists(SoftwareType t, string type, int count, string last)
    {
        var list = HardwarePorts.FixedCandidates(t, "SetMotor", Hw(type), docked: false)!;
        Assert.Equal(count, list.Length);
        Assert.Equal(last, list[^1]);
    }

    [Fact]
    public void C1SpecialFunctions()
    {
        Assert.Equal(["_IIC1_", "_IIC2_"], HardwarePorts.FixedCandidates(SoftwareType.C1RCU, "GetUltrasound", Hw("_PORT_"), false)!);
        Assert.Equal(5, HardwarePorts.FixedCandidates(SoftwareType.C1RCU, "Set3CLed", Hw("_PORT_"), true)!.Length);
        Assert.Equal(["_S1_", "_S2_"], HardwarePorts.FixedCandidates(SoftwareType.C1RCU, "SetMotorServo", Hw("_MOTOR_"), true)!);
        // popup editor has no Set3CLed/SetMotorServo special case
        Assert.Equal(["_M1_", "_M2_"], HardwarePorts.FixedCandidates(SoftwareType.C1RCU, "SetMotorServo", Hw("_MOTOR_"), false)!);
    }

    [Fact]
    public void DockedPerTypeAndFallback()
    {
        Assert.Equal(3, HardwarePorts.FixedCandidates(SoftwareType.M2RCU, "x", Hw("_MOTOR_"), true)!.Length);
        Assert.Equal(2, HardwarePorts.FixedCandidates(SoftwareType.MRCU, "x", Hw("_MOTOR_"), true)!.Length);
        // X3/X4 use the project's hardware table
        Assert.Null(HardwarePorts.FixedCandidates(SoftwareType.X4RCU, "x", Hw("Motor"), true));
        // enum param on E7 docked: empty (original passes a null type)
        var en = new ParameterClass { Name = "e", Type = "Color", IsEnumType = true };
        Assert.Empty(HardwarePorts.FixedCandidates(SoftwareType.E7RCU, "x", en, true)!);
        Assert.Null(HardwarePorts.FixedCandidates(SoftwareType.E6RCU, "x", en, false));
    }

    [Fact]
    public void TableInstances()
    {
        var table = new HardwareTable();
        var item = new HardwareItem { HardwareType = new HardwareType { Name = "Motor" } };
        item.InstanceList!.Add(new HardwareInstance { TypeName = "Motor", Name = "MotorL_" });
        table.HardwareItemList!.Add(item);
        Assert.Equal(["MotorL_"], HardwarePorts.TableInstances(table, Hw("Motor")));
        Assert.Empty(HardwarePorts.TableInstances(table, Hw("Servo")));
    }
}

public class ExpressionCheckerTests
{
    [Theory]
    [InlineData("a > 10 && b < 3")]
    [InlineData("!(a == 1)")]
    [InlineData("x = x + 1")]
    [InlineData("i++")]
    [InlineData("")]
    [InlineData("count")]
    public void Valid(string e) => Assert.True(ExpressionChecker.Check(e).IsValid);

    [Theory]
    [InlineData("a >", ExpressionChecker.ErrorType.WrongLast)]
    [InlineData("* a", ExpressionChecker.ErrorType.WrongInitial)]
    [InlineData("(a > 1", ExpressionChecker.ErrorType.WrongParenthesis)]
    [InlineData("a & b", ExpressionChecker.ErrorType.WrongWord)]
    [InlineData("a $ b", ExpressionChecker.ErrorType.WrongWord)]
    [InlineData("a b", ExpressionChecker.ErrorType.Other)]
    [InlineData("while > 1", ExpressionChecker.ErrorType.Other)]
    [InlineData("a b + c", ExpressionChecker.ErrorType.Dislocation)]
    public void Invalid(string e, ExpressionChecker.ErrorType err) => Assert.Equal(err, ExpressionChecker.Check(e).Error);
}

public class CallTextAfterEditTests
{
    [Fact]
    public void ParameterAndReturnEditsRebuildCode()
    {
        var callee = new ProjectInfo { ProjectName = "GetLight", ReturnType = new ReturnType { Type = "long" } };
        callee.ParamList!.Add(new ParameterClass { Name = "port", Type = "_PORT_", IsHardwareType = true, DefaultValue = "_P1_" });
        var f = FuncCallText.CreateFuncData(callee);
        Assert.Equal("GetLight(_P1_);", f.Code);
        f.ValueList![0] = HardwarePorts.FixedCandidates(SoftwareType.E6RCU, "GetLight", callee.ParamList[0], false)![2];
        f.ReturnName = "var1";
        FuncCallText.SetCodeStr(f, callee);
        Assert.Equal("var1 = GetLight(_P3_);", f.Code);
    }
}

public class VariableRulesTests
{
    [Fact]
    public void NextNameSkipsBothLists()
    {
        var local = new List<VarStruct> { new() { Name = "var0" } };
        var global = new List<VarStruct> { new() { Name = "var1" } };
        var n = 0;
        Assert.Equal("var2", VariableRules.NextName("var", ref n, local, global));
        Assert.Equal(3, n);
    }

    [Fact]
    public void RenameChecks()
    {
        var own = new List<VarStruct> { new() { Name = "a" }, new() { Name = "b" } };
        var other = new List<VarStruct> { new() { Name = "g_1" } };
        bool Sys(string s) => s == "SetMotor";
        Assert.Equal(VariableRules.NameError.None, VariableRules.CheckName(own, 0, other, "a", Sys));
        Assert.Equal(VariableRules.NameError.Exists, VariableRules.CheckName(own, 0, other, "b", Sys));
        Assert.Equal(VariableRules.NameError.Exists, VariableRules.CheckName(own, 0, other, "g_1", Sys));
        Assert.Equal(VariableRules.NameError.Empty, VariableRules.CheckName(own, 0, other, "", Sys));
        Assert.Equal(VariableRules.NameError.WrongName, VariableRules.CheckName(own, 0, other, "1x", Sys));
        Assert.Equal(VariableRules.NameError.KeyWord, VariableRules.CheckName(own, 0, other, "while", Sys));
        Assert.Equal(VariableRules.NameError.SystemUsed, VariableRules.CheckName(own, 0, other, "inport", Sys));
        Assert.Equal(VariableRules.NameError.SystemFunction, VariableRules.CheckName(own, 0, other, "SetMotor", Sys));
        Assert.Equal(VariableRules.NameError.None, VariableRules.CheckName(own, 0, other, "SetMotor", null)); // global rule set
    }

    [Fact]
    public void DefaultsAndTypes()
    {
        Assert.Equal("long", VariableRules.DefaultType(SoftwareType.X4RCU));
        Assert.True(VariableRules.IsValidDefault("long", "", SoftwareType.E6RCU));
        Assert.False(VariableRules.IsValidDefault("unsigned char", "300", SoftwareType.E6RCU));
    }
}

public class NewProjectRulesTests
{
    [Fact]
    public void DefaultNameAndValidation()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Assert.Equal("JMApp1", NewProjectRules.DefaultName(dir, false, false));
            Directory.CreateDirectory(Path.Combine(dir, "JMApp1"));
            Assert.Equal("JMApp2", NewProjectRules.DefaultName(dir, false, false));
            File.WriteAllText(Path.Combine(dir, "JMSub1.rcu"), "");
            Assert.Equal("JMSub2", NewProjectRules.DefaultName(dir, true, true));
            Assert.Equal(Path.Combine(dir, "X", "X.rcu"), NewProjectRules.TargetPath(dir, "X", false));

            bool Sys(string s) => s == "SetMotor";
            Assert.Equal(NewProjectRules.Error.Exists, NewProjectRules.Validate(dir, "JMApp1", false, false, Sys));
            Assert.Equal(NewProjectRules.Error.Blank, NewProjectRules.Validate(dir, "a b", false, false, Sys));
            Assert.Equal(NewProjectRules.Error.SystemName, NewProjectRules.Validate(dir, "SetMotor", false, false, Sys));
            Assert.Equal(NewProjectRules.Error.InvalidChar, NewProjectRules.Validate(dir, "my-sub", true, false, Sys));
            Assert.Equal(NewProjectRules.Error.FirstCharInvalid, NewProjectRules.Validate(dir, "1sub", true, false, Sys));
            Assert.Equal(NewProjectRules.Error.None, NewProjectRules.Validate(dir, "my-app", false, false, Sys));
            Assert.Equal(NewProjectRules.Error.BadPath, NewProjectRules.Validate("relative", "a", false, false, Sys));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParameterRules()
    {
        var hw = new List<HardwareItem> { new() { HardwareType = new HardwareType { Name = "Motor" } } };
        var p = new ParameterClass();
        ProjectInfoRules.Apply(p, "m", "Motor", "", "1", "0", "5", hw);
        Assert.True(p.IsHardwareType);
        Assert.Equal("", p.DefaultValue);
        ProjectInfoRules.Apply(p, "c", "Color", "", "", "", "", hw);
        Assert.True(p.IsEnumType);
        Assert.Equal("Xwb:ProjectInfoFrm_MinBiggerMax", ProjectInfoRules.CheckParamDefine("long", "", "5", "1", SoftwareType.E6RCU));
        Assert.Equal("Xwb:ProjectInfoFrm_ErrLessMin", ProjectInfoRules.CheckParamDefine("long", "0", "1", "5", SoftwareType.E6RCU));
        Assert.Null(ProjectInfoRules.CheckParamDefine("long", "3", "1", "5", SoftwareType.E6RCU));
        Assert.Equal("Xwb:ProjectInfoFrm_SameParaExist", ProjectInfoRules.CheckParamName([new ParameterClass { Name = "a" }], -1, "a"));
    }
}
