using System.Text;
using RoboExp.Core.Build;
using RoboExp.Core.Install;
using RoboExp.Core.Library;
using RoboExp.Core.Model;
using RoboExp.Tests.Build;

namespace RoboExp.Tests.Library;

public class SystemLibraryTests
{
    [Fact]
    public void Stub_header_matches_CreateSystemRcuFile()
    {
        Assert.Equal("#ifndef _SETMOTOR_\r\n#define _SETMOTOR_\r\n#include <JMLib.c>\r\n#endif\r\n",
            SystemLibrary.StubHeaderText("SetMotor", RobotType.Parse("E6RCU")));
        Assert.Equal("#ifndef _GETIN_\r\n#define _GETIN_\r\n#include <JMLib32.c>\r\n#endif\r\n",
            SystemLibrary.StubHeaderText("GetIn", new RobotType(SoftwareType.QRCU)));
    }

    [Theory]
    [InlineData(UiLanguage.Default, "Motor")]
    [InlineData(UiLanguage.CHS, "马达")]
    [InlineData(UiLanguage.CHT, "Motor")]
    public void Localized_text_falls_back_to_default_when_culture_missing_or_empty(UiLanguage lang, string expected) =>
        Assert.Equal(expected, new LocalizedText("Motor", "马达", "").Get(lang));

    [Fact]
    public void Hardware_type_is_underscore_wrapped() =>
        Assert.Equal([true, false, false],
            new[] { "_MOTOR_", "int", "_X" }.Select(t => new LibParam("p", t, "", "", "", LocalizedText.Empty).IsHardwareType));

    [VendorFact]
    public void Loads_real_E6RCU_library_palette_and_stubs()
    {
        var install = Vendor.Install!;
        var type = RobotType.Parse("E6RCU");
        var lib = SystemLibrary.Load(install, type);

        Assert.True(lib.Functions.Count > 100);
        var motor = lib.Find("SetMotor");
        Assert.NotNull(motor);
        Assert.Equal("Motor", motor!.Caption.Get(UiLanguage.Default));
        Assert.Equal("马达", motor.Caption.Get(UiLanguage.CHS));
        Assert.Equal("void", motor.ReturnType);
        Assert.Equal(["which", "speed"], motor.Params.Select(p => p.Name));
        Assert.True(motor.Params[0].IsHardwareType);
        Assert.Equal(("_M1_", "_M4_", "_M1_"), (motor.Params[0].Min, motor.Params[0].Max, motor.Params[0].Default));
        Assert.Equal("Motor channel M1-M4", motor.Params[0].Description.Get(UiLanguage.Default));
        Assert.Equal(("-100", "100"), (motor.Params[1].Min, motor.Params[1].Max));
        Assert.NotNull(motor.IconPath);
        Assert.True(File.Exists(motor.IconPath));

        Assert.NotEmpty(lib.Bands);
        Assert.Equal("Performer", lib.Bands[0].Caption.Get(UiLanguage.Default));
        Assert.Contains("SetMotor", lib.Bands[0].FunctionNames);
        Assert.True(lib.IsSystemFunction("SetMotor"));
        Assert.False(lib.IsSystemFunction("setmotor"));
        Assert.False(lib.IsSystemFunction("NotAFunction"));

        var stub = Path.Combine(lib.StubIncludeDirectory, "SetMotor.h");
        Assert.Equal(SystemLibrary.StubHeaderText("SetMotor", type), File.ReadAllText(stub, Encoding.ASCII));
        Assert.All(lib.Functions, f => Assert.True(File.Exists(Path.Combine(lib.StubIncludeDirectory, f.Name + ".h"))));

        // The vendor ships "Jmlib.c" but stubs and examples include "JMLib.c": an exact-case alias forwards to it.
        var alias = Path.Combine(lib.StubIncludeDirectory, "JMLib.c");
        Assert.Contains(Directory.EnumerateFiles(lib.StubIncludeDirectory), f => Path.GetFileName(f) == "JMLib.c");
        var forwarded = File.ReadAllText(alias).Trim();
        Assert.StartsWith("#include \"", forwarded);
        Assert.True(File.Exists(forwarded["#include \"".Length..^1]));
    }

    [VendorFact]
    public void Every_supported_type_loads_and_load_is_idempotent()
    {
        foreach (var type in RobotType.Supported)
        {
            var lib = SystemLibrary.Load(Vendor.Install!, type);
            Assert.NotEmpty(lib.Functions);
            Assert.NotEmpty(lib.Bands);
            var stamp = File.GetLastWriteTimeUtc(Path.Combine(lib.StubIncludeDirectory, lib.Functions[0].Name + ".h"));
            SystemLibrary.Load(Vendor.Install!, type);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(lib.StubIncludeDirectory, lib.Functions[0].Name + ".h")));
        }
    }
}
