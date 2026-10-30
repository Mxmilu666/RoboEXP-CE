using RoboExp.Core.Build;
using RoboExp.Core.Install;
using RoboExp.Core.Library;

namespace RoboExp.Tests.Build;

public class FlashRegionTests
{
    [Fact]
    public void Parses_FLASH_ignoring_commented_out_regions()
    {
        const string script = """
            MEMORY
            {
              /*FLASH (rx)      : ORIGIN = 0x08000000, LENGTH = 512K*/
              FLASH (rx)      : ORIGIN = 0x08031000, LENGTH = 128K
              RAM (xrw)       : ORIGIN = 0x20000000, LENGTH = 64K
            }
            """;
        Assert.Equal(new FlashRegion(0x08031000, 128 * 1024, "s.ld"), LinkerScripts.ParseFlashRegion(script, "s.ld"));
        Assert.Null(LinkerScripts.ParseFlashRegion("MEMORY { RAM (xrw) : ORIGIN = 0x20000000, LENGTH = 64K }", "r.ld"));
    }

    [VendorFact]
    public void Vendor_flash_regions_follow_INCLUDE_and_case()
    {
        var builder = new Builder(Vendor.Install!, ToolchainLocator.Default);
        Assert.Equal((0x08031000UL, 64UL * 1024), Region(builder, "E2RCU"));   // stm32.ld (INCLUDEs STM32_SEC_FLASH.ld)
        Assert.Equal((0x081A0000UL, 256UL * 1024), Region(builder, "E7RCU"));  // vendor dir is "include", template says "Include"
        Assert.Equal((0x08031000UL, 128UL * 1024), Region(builder, "MRCU"));
        Assert.Equal((0x08010000UL, 64UL * 1024), Region(builder, "C1RCU"));   // -TSTM32F072CBTx_FLASH.ld (no space)
    }

    private static (ulong, ulong) Region(Builder builder, string type)
    {
        var r = builder.GetFlashRegion(RobotType.Parse(type));
        Assert.NotNull(r);
        return (r!.Origin, r.Length);
    }
}

public class RealToolchainTests
{
    [ToolchainFact]
    public async Task Builds_vendor_example_for_every_type_within_flash()
    {
        var install = Vendor.Install!;
        var builder = new Builder(install, ToolchainLocator.Default) { Language = UiLanguage.CHS };
        var temp = Directory.CreateTempSubdirectory("rbx-real-").FullName;
        try
        {
            foreach (var type in RobotType.Supported)
            {
                // First example per type that the native toolchain builds; each type ships several stale ones.
                string? built = null;
                foreach (var dir in Directory.EnumerateDirectories(install.ExamplesDirectory(type), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(dir);
                    if (VendorInstall.FindChildIgnoreCase(dir, name + ".c") is not { } src) continue;
                    var target = Path.Combine(temp, type.Name, name);
                    Directory.CreateDirectory(target);
                    foreach (var f in Directory.EnumerateFiles(dir).Where(f => !f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
                        File.Copy(f, Path.Combine(target, Path.GetFileName(f)), overwrite: true);
                    var result = await builder.BuildAsync(Path.Combine(target, Path.GetFileName(src)), type, BuildGroup.Main);
                    if (!result.Success) continue;
                    var size = (ulong)new FileInfo(result.BinaryPath!).Length;
                    Assert.True(size > 1024, $"{type.Name}/{name}: {size} bytes");
                    Assert.True(size <= builder.GetFlashRegion(type)!.Length, $"{type.Name}/{name}: {size} bytes exceed FLASH");
                    Assert.DoesNotContain(result.Diagnostics, d => d.IsError);
                    Assert.False(File.Exists(Path.ChangeExtension(result.BinaryPath!, ".o")));
                    built = name;
                    break;
                }
                Assert.True(built != null, $"no {type.Name} example builds");
            }
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [ToolchainFact]
    public async Task Compile_error_is_located_in_project_source_and_localized()
    {
        var install = Vendor.Install!;
        var builder = new Builder(install, ToolchainLocator.Default) { Language = UiLanguage.CHS };
        var dir = Directory.CreateTempSubdirectory("rbx-err-").FullName;
        try
        {
            var src = Path.Combine(dir, "Bad.c");
            File.WriteAllText(src, "#include \"JMLib.c\"\n#include <SetMotor.h>\nint main(void)\n{\n    SetMotor(_M1_);\n    undefined_thing = 1;\n}\n");
            var result = await builder.BuildAsync(src, RobotType.Parse("E6RCU"), BuildGroup.Main);

            Assert.False(result.Success);
            Assert.Null(result.BinaryPath);
            Assert.Contains(result.Diagnostics, d => d.IsError && d.File == src && d.Line == 5 && d.Message.Contains("too few arguments to function 'SetMotor'"));
            Assert.Contains(result.Diagnostics, d => d.IsError && d.File == src && d.Line == 6 && d.Message.Contains("undeclared"));
            Assert.Contains("(该模块参数没有填写完整，可能由更新模块参数引起)too few arguments to function", result.LocalizedLog);
            Assert.DoesNotContain("该模块", result.Log);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
