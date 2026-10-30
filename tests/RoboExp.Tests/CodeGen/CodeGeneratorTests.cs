using RoboExp.Core.CodeGen;
using RoboExp.Core.Flow;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;

namespace RoboExp.Tests.CodeGen;

public class CodeGeneratorTests
{
    private static string Lines(params string[] lines) => string.Concat(lines.Select(l => l + "\r\n"));

    private static CodeGenResult Gen(ProjectData data, FlowBuilder fb, SoftwareType type, Func<string, bool>? isSystem = null) =>
        CodeGenerator.Generate(data, new FlowGraph(fb.Objects), new CodeGenOptions(type, OuterType.Entity, isSystem));

    [Fact]
    public void Sequence_AppWithVariablesFunctionsAndEmptyReturn()
    {
        var data = FlowBuilder.Project(ProjectType.FlowApp, "Prog", SoftwareType.E2RCU);
        data.VarList!.Add(new VarStruct { Type = "int", Name = "a", DefaultValue = "1", Description = "counter" });
        data.VarList.Add(new VarStruct { Type = "int", Name = "p", IsParam = true });
        var fb = new FlowBuilder();
        var start = fb.Icon(IconType.Start);
        var calc = fb.Calc("a = a + 1", "inc");
        var motor = new FuncData { ProjectName = "SetMotor", ValueList = new(["1", "a"]) };
        FuncCallText.SetCodeStr(motor, null);
        var f1 = fb.Icon(IconType.Function, motor);
        var sub = new FuncData { ProjectName = "MySub" };
        FuncCallText.SetCodeStr(sub, null);
        var f2 = fb.Icon(IconType.Function, sub);
        var ret = fb.Icon(IconType.Return);
        fb.Chain(start, calc, f1, f2, ret);

        var graph = new FlowGraph(fb.Objects);
        var result = CodeGenerator.Generate(data, graph, new CodeGenOptions(SoftwareType.E2RCU, IsSystemFunction: n => n == "SetMotor"));

        Assert.Equal(Lines(
            "",
            "#include \"HardwareInfo.c\"",
            "#include \"JMLib.c\"",
            "#include <SetMotor.h>",
            "#include \"MySub.c\"",
            "int main(void)",
            "{",
            "    E2RCU_Init();",
            "    int a = 1;   //counter",
            "    //inc",
            "    a = a + 1;",
            "    SetMotor(1, a);",
            "    MySub();",
            "    return ;",
            "    while(1);",
            "}",
            ""), result.Text);
        Assert.Equal([f1, f2], result.UsedFunctionIcons);
        Assert.Equal(6, graph.State(start).BeginLine);
        Assert.Equal(11, graph.State(calc).BeginLine);
        Assert.Equal(14, graph.State(ret).BeginLine);
        Assert.Same(calc, graph.FindObjectAtLine(11));
        Assert.True(graph.State(ret).IsInCode);
    }

    [Fact]
    public void NestedIfElseWhileFor_FlowFunc()
    {
        var data = FlowBuilder.Project(ProjectType.FlowFunc, "Sub");
        var fb = new FlowBuilder();
        var start = fb.Icon(IconType.Start);
        var bif = fb.Icon(IconType.BeginIf, new BeginIfData { Expression = "x > 0" });
        var bwhile = fb.Icon(IconType.BeginWhile, new BeginWhileData { Expression = "  " });
        var dec = fb.Calc("x--");
        var ewhile = fb.Icon(IconType.EndWhile, null, "never emitted");
        var bfor = fb.Icon(IconType.BeginFor, new BeginForData { LoopCountStr = "3" });
        var empty = fb.Calc("");
        var efor = fb.Icon(IconType.EndFor);
        var eif = fb.Icon(IconType.EndIf, null, "after if");
        var ret = fb.Icon(IconType.Return, new ReturnData { Expression = "x" });
        fb.Wire(start, PortType.Start, bif, PortType.In);
        fb.Wire(bif, PortType.BeginIf, bwhile, PortType.In, segments: 3);
        fb.Wire(bwhile, PortType.BeginWhile, dec, PortType.In);
        fb.Wire(dec, PortType.Out, ewhile, PortType.EndWhile);
        fb.Wire(ewhile, PortType.Out, eif, PortType.EndIf);
        fb.Wire(bif, PortType.BeginElse, bfor, PortType.In);
        fb.Wire(bfor, PortType.BeginFor, empty, PortType.In);
        fb.Wire(empty, PortType.Out, efor, PortType.EndFor);
        fb.Wire(efor, PortType.Out, eif, PortType.EndElse);
        fb.Wire(eif, PortType.Out, ret, PortType.In);

        var graph = new FlowGraph(fb.Objects);
        var text = CodeGenerator.Generate(data, graph, new CodeGenOptions(SoftwareType.MRCU)).Text;

        Assert.Equal(Lines(
            "#ifndef _SUB_",
            "#define _SUB_",
            "",
            "#include \"HardwareInfo.c\"",
            "void Sub()",
            "{",
            "    if ( x > 0 )",
            "    {",
            "        while (1)",
            "        {",
            "            x--;",
            "        }",
            "        //never emitted",
            "    }",
            "    else",
            "    {",
            "        for (int _FOR_0_ = 0; _FOR_0_ < 3; _FOR_0_++ )",
            "        {",
            "            ",
            "        }",
            "    }",
            "    //after if",
            "    return x;",
            "}",
            "#endif",
            ""), text);
        Assert.Same(eif, graph.State(bif).EndIf);
        Assert.Same(eif, graph.State(bif).EndElse);
        Assert.Same(ewhile, graph.State(bwhile).EndWhile);
        Assert.Same(efor, graph.State(bfor).EndWhile);
        Assert.Equal(7, graph.State(bif).BeginLine);
        Assert.Equal(22, graph.State(bif).EndLine);
        Assert.Same(bif, graph.FindObjectAtLine(7));
        Assert.Equal("for (int _FOR_0_ = 0; _FOR_0_ < 3; _FOR_0_++ )", bfor.DataObject!.Code);
        Assert.All(fb.Objects.List!.Where(o => o.DrawObject is LineDraw), l => Assert.Equal(LineState.Normal, graph.State(l).LineState));
    }

    [Fact]
    public void IfWithoutEnd_IsSkippedAndMismatchedEndMarksWireInvalid()
    {
        var data = FlowBuilder.Project(ProjectType.FlowApp, "P", SoftwareType.MRCU);
        var fb = new FlowBuilder();
        var start = fb.Icon(IconType.Start);
        var bwhile = fb.Icon(IconType.BeginWhile);
        var efor = fb.Icon(IconType.EndFor);
        fb.Wire(start, PortType.Start, bwhile, PortType.In);
        fb.Wire(bwhile, PortType.BeginWhile, efor, PortType.EndFor);
        var graph = new FlowGraph(fb.Objects);
        var text = CodeGenerator.Generate(data, graph, new CodeGenOptions(SoftwareType.MRCU)).Text;
        Assert.DoesNotContain("while", text.Replace("while(1);", ""));
        Assert.False(graph.State(bwhile).IsInCode);
        var wire = fb.Objects.List!.Last(o => o.DrawObject is LineDraw);
        Assert.Equal(LineState.Invalid, graph.State(wire).LineState);
    }

    private static (ProjectData, FlowBuilder) TwoStarts(SoftwareType type, bool secondConnected = true)
    {
        var data = FlowBuilder.Project(ProjectType.FlowApp, "P", type);
        var fb = new FlowBuilder();
        var s1 = fb.Icon(IconType.Start);
        var a = fb.Calc("a()");
        var s2 = fb.Icon(IconType.Start);
        var b = fb.Calc("b()");
        fb.Chain(s1, a);
        if (secondConnected) fb.Chain(s2, b);
        return (data, fb);
    }

    [Theory]
    [InlineData(SoftwareType.X4RCU, "#include <stm32f4xx.h>")]
    [InlineData(SoftwareType.E3RCU, "#include <stm32f10x.h>")]
    [InlineData(SoftwareType.E6RCU, "#include <stm32f4xx_hal.h>")]
    [InlineData(SoftwareType.E7RCU, "#include <stm32h7xx_hal.h>")]
    public void MultiStart_LaterStartsComeFirst(SoftwareType type, string chip)
    {
        var (data, fb) = TwoStarts(type);
        Assert.Equal(Lines(chip, "", "#include \"HardwareInfo.c\"", "#include \"JMLib.c\"", "int main(void)", "{",
            "    " + type + "_Init();", "    b();", "    a();", "    while(1);", "}", ""), Gen(data, fb, type).Text);
    }

    [Fact]
    public void MultiStart_StopsAtFirstUnconnectedStart()
    {
        var data = FlowBuilder.Project(ProjectType.FlowApp, "P", SoftwareType.X4RCU);
        var fb = new FlowBuilder();
        fb.Icon(IconType.Start);
        var s1 = fb.Icon(IconType.Start);
        var a = fb.Calc("a()");
        fb.Chain(s1, a);
        Assert.DoesNotContain("a();", Gen(data, fb, SoftwareType.X4RCU).Text);
        // The standard path only looks at the first Start icon, which leads nowhere either.
        Assert.DoesNotContain("a();", Gen(FlowBuilder.Project(ProjectType.FlowApp, "P", SoftwareType.MRCU), fb, SoftwareType.MRCU).Text);
    }

    [Theory]
    [InlineData(SoftwareType.MRCU, "    MRCU_Init();")]
    [InlineData(SoftwareType.C1RCU, "   C1RCU_Init();")]
    [InlineData(SoftwareType.IQ2Robot, "    IQRobot_Init();")]
    [InlineData(SoftwareType.M6RCU, "    M6RCU_Init();")]
    public void StandardPath_OnlyFirstStart_AndInitLines(SoftwareType type, string init)
    {
        var (data, fb) = TwoStarts(type);
        var text = Gen(data, fb, type).Text;
        Assert.Contains("{\r\n" + init + "\r\n    a();\r\n    while(1);\r\n}\r\n", text);
        Assert.DoesNotContain("b();", text);
    }

    [Fact]
    public void GlobalIncludeSuppressesDuplicateAndGlobalsAndCodeEditor()
    {
        var data = FlowBuilder.Project(ProjectType.FlowApp, "P", SoftwareType.MRCU);
        data.CodeFrameData!.Global = "#define N 3\r\n// see also \"mysub.c\"";
        data.ProjectInfo!.CurUsedGlobalVarList!.Add(new VarStruct { Type = "int", Name = "g", DefaultValue = "2" });
        var fb = new FlowBuilder();
        var start = fb.Icon(IconType.Start);
        var sub = new FuncData { ProjectName = "MySub" };
        FuncCallText.SetCodeStr(sub, null);
        var call = fb.Icon(IconType.Function, sub);
        var code = fb.Icon(IconType.CodeEditor, new CodeEditorData { Codes = new(["x = 1;\r\n", "y = 2;\r\n"]) });
        fb.Chain(start, call, code);
        var graph = new FlowGraph(fb.Objects);
        var text = CodeGenerator.Generate(data, graph, new CodeGenOptions(SoftwareType.MRCU)).Text;
        Assert.Equal(Lines("#define N 3", "// see also \"mysub.c\"", "", "#include \"HardwareInfo.c\"", "#include \"JMLib.c\"",
            "// define global var", "int g = 2;", "", "int main(void)", "{", "    MRCU_Init();", "    MySub();", "    x = 1;", "    y = 2;",
            "    while(1);", "}", ""), text);
        Assert.Equal(13, graph.State(code).BeginLine);
        Assert.Equal(-1, graph.State(code).EndLine); // original sets EndLine only for more than two lines
    }

    [Fact]
    public void SimulationOuterTypeOmitsHardwareInfo()
    {
        var (data, fb) = TwoStarts(SoftwareType.MRCU);
        var text = CodeGenerator.Generate(data, new FlowGraph(fb.Objects), new CodeGenOptions(SoftwareType.MRCU, OuterType.Simulation)).Text;
        Assert.DoesNotContain("HardwareInfo", text);
    }

    [Fact]
    public void ReturnWithNullExpression()
    {
        var data = FlowBuilder.Project(ProjectType.FlowFunc, "F");
        var fb = new FlowBuilder();
        var start = fb.Icon(IconType.Start);
        var ret = fb.Icon(IconType.Return, new ReturnData { Expression = null });
        fb.Chain(start, ret);
        Assert.Contains("\r\n    return;\r\n", Gen(data, fb, SoftwareType.MRCU).Text);
    }

    [Fact]
    public void SetCodeStr_ReturnPrefixRules()
    {
        var callee = new ProjectInfo { ProjectName = "Get" };
        callee.ReturnType!.Type = "int";
        callee.ParamList!.Add(new ParameterClass { Name = "a", Type = "int", DefaultValue = "1" });
        callee.ParamList.Add(new ParameterClass { Name = "b", Type = "int" });
        var data = FuncCallText.CreateFuncData(callee);
        Assert.Equal(["1", ""], data.ValueList!);
        Assert.Equal("Get(1, );", data.Code);
        data.ReturnName = "r";
        data.ValueList[1] = "2";
        Assert.Equal("r = Get(1, 2);", FuncCallText.SetCodeStr(data, callee));
        Assert.Equal("r = Get(1, 2);", data.Code);
        data.ReturnName = "void";
        Assert.Equal("Get(1, 2);", FuncCallText.SetCodeStr(data, callee));
        data.ReturnName = "r";
        callee.ReturnType.Type = "void";
        Assert.Equal("Get(1, 2);", FuncCallText.SetCodeStr(data, callee));
    }

    [Fact]
    public void PreCompileChecks_ParamErrorAndReturnWarning()
    {
        var data = FlowBuilder.Project(ProjectType.FlowApp, "P", SoftwareType.MRCU);
        var callee = new ProjectInfo { ProjectName = "Get" };
        callee.ReturnType!.Type = "int";
        callee.ParamList!.Add(new ParameterClass { Name = "a", Type = "int" });
        data.UsedProjectInfoList!.Add(callee);
        var fb = new FlowBuilder();
        var start = fb.Icon(IconType.Start);
        var call = fb.Icon(IconType.Function, FuncCallText.CreateFuncData(callee));
        fb.Chain(start, call);
        var graph = new FlowGraph(fb.Objects);
        var used = CodeGenerator.Generate(data, graph, new CodeGenOptions(SoftwareType.MRCU)).UsedFunctionIcons;
        var report = PreCompileChecks.Run(data, graph, used, SoftwareType.MRCU, @"C:\p\P.c");
        var issue = Assert.Single(report.Issues);
        Assert.Same(call, issue.Object);
        Assert.Equal(@"C:\p\P.c:8: error:Too few arguments to Subroutine ""Get""", issue.Text);

        ((FuncData)call.DataObject!).ValueList![0] = "5";
        report = PreCompileChecks.Run(data, graph, used, SoftwareType.MRCU, @"C:\p\P.c");
        issue = Assert.Single(report.Issues);
        Assert.False(report.HasError);
        Assert.Equal(PreCompileIssueKind.ReturnVarNotSet, issue.Kind);
        Assert.Equal(@"C:\p\P.c:8: warning:Return variable has not been set ""Get""", issue.Text);
    }

    [Fact]
    public void PreCompileChecks_HardwareInstanceOnX4()
    {
        var data = FlowBuilder.Project(ProjectType.FlowApp, "P", SoftwareType.X4RCU);
        var callee = new ProjectInfo { ProjectName = "GetTouch" };
        callee.ParamList!.Add(new ParameterClass { Name = "port", Type = "_TOUCH_", IsHardwareType = true });
        data.UsedProjectInfoList!.Add(callee);
        var fb = new FlowBuilder();
        var start = fb.Icon(IconType.Start);
        var fd = FuncCallText.CreateFuncData(callee);
        fd.ValueList![0] = "_TOUCH_1_";
        var call = fb.Icon(IconType.Function, fd);
        fb.Chain(start, call);
        var graph = new FlowGraph(fb.Objects);
        var used = CodeGenerator.Generate(data, graph, new CodeGenOptions(SoftwareType.X4RCU)).UsedFunctionIcons;
        var issue = Assert.Single(PreCompileChecks.Run(data, graph, used, SoftwareType.X4RCU, "P.c").Issues);
        Assert.Equal(PreCompileIssueKind.HardwareInstanceNotDefined, issue.Kind);
        Assert.Equal("Port _TOUCH_1_ undeclared.", issue.Text);
        Assert.Empty(PreCompileChecks.Run(data, graph, used, SoftwareType.MRCU, "P.c").Issues);
    }
}
