using System.Text;
using RoboExp.Core.CodeGen;
using RoboExp.Core.Install;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;
using RoboExp.Core.Project;
using RoboExp.Core.Rcu;
using RoboExp.Tests.Build;

namespace RoboExp.Tests.CodeGen;

public class HardwareInfoAndProjectTests
{
    private static ProjectData App(SoftwareType type, string allCode)
    {
        var data = FlowBuilder.Project(ProjectType.FlowApp, "P", type);
        data.CodeFrameData!.AllCode = allCode;
        return data;
    }

    [Fact]
    public void Task1StubForMultiTaskApps()
    {
        Assert.Equal("#ifndef _TASK1_ \n#define _TASK1_ \nvoid task1(){}\n #endif \n",
            HardwareInfoFile.Create(App(SoftwareType.E6RCU, "int main(void)"), SoftwareType.E6RCU, RcuInfo.Empty));
        Assert.Equal("", HardwareInfoFile.Create(App(SoftwareType.X4RCU, "void task1(void){}"), SoftwareType.X4RCU, RcuInfo.Empty));
        Assert.Equal("", HardwareInfoFile.Create(App(SoftwareType.MRCU, ""), SoftwareType.MRCU, RcuInfo.Empty));
        var func = App(SoftwareType.E7RCU, "");
        func.ProjectInfo!.ProjectType = ProjectType.FlowFunc;
        Assert.Equal("", HardwareInfoFile.Create(func, SoftwareType.E7RCU, RcuInfo.Empty));
    }

    [Fact]
    public void HardwareTableTypesThenStubThenInstances()
    {
        var data = App(SoftwareType.X4RCU, "");
        var item = new HardwareItem { HardwareType = new HardwareType { Name = "_BTN_" } };
        item.InstanceList!.Add(new HardwareInstance { Name = "_BTN_1_", TypeName = "_BTN_", TypeNum = 3, PinTeam = new PinTeam { CtrlPinNumList = new([1]) } });
        item.InstanceList.Add(new HardwareInstance { Name = "_BTN_2_", TypeName = "_BTN_", PinTeam = new PinTeam { CtrlPinNumList = new([0, 1]) } });
        data.HardwareTable!.HardwareItemList!.Add(item);
        var rcu = new RcuInfo("X4", [new HardPin(1, 3, 0), new HardPin(2, 5, 0)]);
        // _BTN_1_: pin 0 -> (port 2, pin 5): ((5 << 3) | 2) << 4 | 3 = 675
        // _BTN_2_: i=1 -> (2,5) = 42; i=0 -> (1,3): ((42 << 4 | 3) << 3 | 1) << 4 = 86416
        Assert.Equal(675u, HardwareInfoFile.GetHardwareInfo(item.InstanceList[0], rcu, SoftwareType.X4RCU));
        Assert.Equal(86416u, HardwareInfoFile.GetHardwareInfo(item.InstanceList[1], rcu, SoftwareType.X4RCU));
        Assert.Equal(
            "#ifndef _BTN_\r\n#define _BTN_ unsigned long\r\n#endif\r\n" +
            "#ifndef _TASK1_ \n#define _TASK1_ \nvoid task1(){}\n #endif \n" +
            "#ifndef _BTN_1_\r\n#define _BTN_1_ 675\r\n#endif\r\n" +
            "#ifndef _BTN_2_\r\n#define _BTN_2_ 86416\r\n#endif\r\n",
            HardwareInfoFile.Create(data, SoftwareType.X4RCU, rcu));
        Assert.Throws<InvalidDataException>(() => HardwareInfoFile.Create(data, SoftwareType.X4RCU, RcuInfo.Empty));
    }

    [Fact]
    public void WriteTextFileIsGbkVerbatim()
    {
        var path = Path.Combine(Path.GetTempPath(), "roboexp-gbk-" + Guid.NewGuid().ToString("N") + ".c");
        try
        {
            HardwareInfoFile.WriteTextFile(path, "//中文\r\nint a;\r\n");
            Assert.Equal(new byte[] { 0x2F, 0x2F, 0xD6, 0xD0, 0xCE, 0xC4, 0x0D, 0x0A, (byte)'i', (byte)'n', (byte)'t', (byte)' ', (byte)'a', (byte)';', 0x0D, 0x0A },
                File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CodeProjectSourceText()
    {
        var app = FlowBuilder.Project(ProjectType.CodeApp, "A");
        app.CodeFrameData!.Code = ProjectSession.CodeAppTemplate(Core.Library.UiLanguage.Default);
        Assert.Equal("//Application temple \r\n\r\nint main(void)\r\n{\r\n\r\n}\r\n", ProjectSession.CodeSourceText(app));
        var func = FlowBuilder.Project(ProjectType.CodeFunc, "Fn");
        func.ProjectInfo!.ParamList!.Add(new ParameterClass { Type = "int", Name = "n" });
        (func.CodeFrameData!.Global, func.CodeFrameData.Code) = ProjectSession.CodeFuncTemplate("Fn", Core.Library.UiLanguage.Default);
        Assert.Equal("//global area\r\n#ifndef _FN_\r\n#define _FN_\r\n\r\n\r\nvoid Fn(int n)\r\n//code area\r\n{\r\n\r\n}\r\n#endif\r\n\r\n",
            ProjectSession.CodeSourceText(func));
    }

    [VendorFact]
    public void CreateNewFlowAppWritesRcuGbkCAndHardwareInfo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "roboexp-new-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = ProjectSession.CreateNew(Path.Combine(dir, "Demo", "Demo.rcu"), ProjectType.FlowApp, RobotType.Parse("E6RCU"), Vendor.Install!);
            var cBytes = File.ReadAllBytes(Path.Combine(dir, "Demo", "Demo.c"));
            Assert.Equal(Encoding.ASCII.GetBytes(
                "#include <stm32f4xx_hal.h>\r\n\r\n#include \"HardwareInfo.c\"\r\n#include \"JMLib.c\"\r\nint main(void)\r\n{\r\n    E6RCU_Init();\r\n    while(1);\r\n}\r\n\r\n"), cBytes);
            Assert.Equal(HardwareInfoFile.Task1Stub, File.ReadAllText(Path.Combine(dir, "Demo", "HardwareInfo.c")));

            var reopened = ProjectSession.Open(session.RcuPath, Vendor.Install!);
            Assert.Equal(ProjectType.FlowApp, reopened.Info.ProjectType);
            Assert.Equal("long", reopened.Info.ReturnType!.Type);
            Assert.Equal(Encoding.ASCII.GetString(cBytes), reopened.Data.CodeFrameData!.AllCode);
            var start = Assert.Single(reopened.DrawingObjects!.List!);
            Assert.Equal(new Point(48, 48), start.DrawObject!.Position);
            Assert.Equal(PortType.Start, Assert.Single(start.DrawObject.Ports!).Type);

            // A Chinese comment survives as GBK in the .c.
            reopened.Data.VarList!.Add(new VarStruct { Type = "int", Name = "x", Description = "中文" });
            reopened.Save();
            var text = Gbk().GetString(File.ReadAllBytes(reopened.CPath));
            Assert.Equal(reopened.Data.CodeFrameData.AllCode, text);
            Assert.Contains("    int x;   //中文\r\n", text);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [VendorFact]
    public void NewCodeFuncProject()
    {
        var dir = Path.Combine(Path.GetTempPath(), "roboexp-new-" + Guid.NewGuid().ToString("N"));
        try
        {
            var s = ProjectSession.CreateNew(Path.Combine(dir, "Fn.rcu"), ProjectType.CodeFunc, RobotType.Parse("MRCU"), Vendor.Install!);
            Assert.Null(s.Graph);
            Assert.Equal("//global area\r\n#ifndef _FN_\r\n#define _FN_\r\n\r\n\r\nvoid Fn()\r\n//code area\r\n{\r\n\r\n}\r\n#endif\r\n\r\n",
                File.ReadAllText(Path.Combine(dir, "Fn.c")));
            Assert.Equal("", File.ReadAllText(Path.Combine(dir, "HardwareInfo.c")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>The 3 vendor examples saved by the current release regenerate byte-identically (others: see gencheck).</summary>
    [VendorFact]
    public void VendorExamplesFromCurrentReleaseRegenerateExactly()
    {
        var matched = 0;
        foreach (var type in new[] { "E6RCU", "E7RCU", "M6RCU" })
        {
            foreach (var path in Directory.EnumerateFiles(Vendor.Install!.ExamplesDirectory(RobotType.Parse(type)), "*.rcu", SearchOption.AllDirectories))
            {
                var file = RcuReader.Open(path);
                if (file.FlowData == null) continue;
                var stored = file.ProjectData!.CodeFrameData!.AllCode;
                if (stored == null || !stored.Contains("#include <stm32")) continue;
                var session = ProjectSession.Create(file, path, Vendor.Install!, RobotType.Parse(type));
                if (session.Regenerate() == stored) matched++;
            }
        }
        Assert.Equal(3, matched); // CompassBTest-style files saved by 6.0.x; the rest predate the current layout (rcu gencheck)
    }

    private static Encoding Gbk()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936);
    }
}
