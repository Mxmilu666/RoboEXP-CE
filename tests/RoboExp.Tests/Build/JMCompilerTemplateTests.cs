using RoboExp.Core.Build;

namespace RoboExp.Tests.Build;

public sealed class JMCompilerTemplateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("jmc-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private TemplateContext Ctx(string? workDir = null) =>
        new(Path.Combine(_dir, "test.c"), workDir ?? _dir, "/inc*" + _dir + "* *", "/inc", "/app/Compiler\\");

    [Fact]
    public void Expands_source_parts_dirs_and_compiler_placeholders()
    {
        var s = JMCompilerTemplate.Expand(
            "  \"?C\\..\\ARM\\bin\\arm-none-eabi-gcc\" ?I -c \"?P\\?N?E\" -o \"?P\\?N.o\" src=?F wd=?W ?L  ", Ctx());

        Assert.Equal(
            $"\"/app/Compiler\\..\\ARM\\bin\\arm-none-eabi-gcc\" -I\"/inc\" -I\"{_dir}\" -c \"{_dir}\\test.c\" -o \"{_dir}\\test.o\" src={_dir}/test.c wd={_dir} -L\"/inc\"",
            s);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("default", "")]
    [InlineData("DEFAULT", "")]
    [InlineData("\"/a\"", "-I\"/a\"")]
    [InlineData(" /a * /b c ** ", "-I\"/a\" -I\"/b c\"")]
    public void Include_argument_follows_GetIncDir(string dirs, string expected) =>
        Assert.Equal(expected, JMCompilerTemplate.IncludeArgument(dirs));

    [Fact]
    public void Quoted_O_lists_object_files_of_work_dir_each_quoted_with_trailing_space()
    {
        File.WriteAllText(Path.Combine(_dir, "b.o"), "");
        File.WriteAllText(Path.Combine(_dir, "a.o"), "");
        File.WriteAllText(Path.Combine(_dir, "a.c"), "");

        var s = JMCompilerTemplate.Expand("gcc \"x.o\" \"?O\" -o out", Ctx());

        Assert.Equal($"gcc \"x.o\" \"{_dir}/a.o\" \"{_dir}/b.o\"  -o out", s);
    }

    [Fact]
    public void Quoted_O_is_removed_when_work_dir_has_no_objects_or_does_not_exist()
    {
        Assert.Equal("gcc  -o out", JMCompilerTemplate.Expand("gcc \"?O\" -o out", Ctx()));
        Assert.Equal("gcc  -o out", JMCompilerTemplate.Expand("gcc \"?O\" -o out", Ctx(Path.Combine(_dir, "missing"))));
    }

    [Fact]
    public void Work_dir_default_is_source_directory_and_missing_dir_becomes_empty()
    {
        var src = Path.Combine(_dir, "x.c");
        Assert.Equal(_dir, JMCompilerTemplate.NormalizeWorkDir("default", src));
        Assert.Equal(_dir, JMCompilerTemplate.NormalizeWorkDir(_dir + "/", src));
        Assert.Equal("", JMCompilerTemplate.NormalizeWorkDir(Path.Combine(_dir, "nope"), src));
    }

    [Fact]
    public void D_and_T_steps_strip_prefix_and_quotes_including_unbalanced_ones()
    {
        var expanded = JMCompilerTemplate.Expand("?D \"?P\\?N.o\"", Ctx());
        Assert.Equal(new BuildStep.Delete($"{_dir}\\test.o"), JMCompilerTemplate.ToStep(expanded));

        // JiMu8 templates really are written with a missing closing quote: <cmd1>?T "?P\?N.o</cmd1>
        expanded = JMCompilerTemplate.Expand("?T \"?P\\?N.o", Ctx());
        Assert.Equal(new BuildStep.Test($"{_dir}\\test.o"), JMCompilerTemplate.ToStep(expanded));

        Assert.IsType<BuildStep.Run>(JMCompilerTemplate.ToStep("gcc -c x.c"));
    }

    [Fact]
    public void Command_line_split_removes_quotes_inside_arguments()
    {
        var args = JMCompilerTemplate.SplitCommandLine("\"/a b/gcc\"  -Wl,-Map=\"/p q/x.map\",-cref -I\"/i\" \"\" last");
        Assert.Equal(["/a b/gcc", "-Wl,-Map=/p q/x.map,-cref", "-I/i", "", "last"], args);
    }

    [Fact]
    public void Config_reads_contiguous_cmd_entries_and_reports_missing_groups()
    {
        var xml = Path.Combine(_dir, "JMCompiler.xml");
        File.WriteAllText(xml, """
            <root>
              <!-- comment -->
              <T1>
                <main><cmd0>a</cmd0><cmd1>b</cmd1><cmd3>skipped after gap</cmd3></main>
                <func><cmd0>f</cmd0></func>
              </T1>
            </root>
            """);
        var config = JMCompilerConfig.Load(xml);

        Assert.Equal(["a", "b"], config.GetCommands("T1", "main"));
        Assert.Equal(["f"], config.GetCommands("T1", JMCompilerConfig.GroupName(BuildGroup.Func)));
        Assert.Null(config.GetCommands("T2", "main"));
        Assert.Null(config.GetCommands("T1", "default"));
    }

    [VendorFact]
    public void Vendor_config_has_main_and_func_for_every_supported_type()
    {
        var config = JMCompilerConfig.Load(Vendor.Install!.CompilerXmlPath);
        foreach (var type in RoboExp.Core.Install.RobotType.Supported)
        {
            var main = config.GetCommands(type.Name, "main");
            Assert.NotNull(main);
            Assert.Contains(main, c => c.Contains("objcopy"));
            Assert.NotNull(config.GetCommands(type.Name, "func"));
        }
    }
}
