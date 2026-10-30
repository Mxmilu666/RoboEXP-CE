using RoboExp.Core.Build;
using RoboExp.Core.Library;

namespace RoboExp.Tests.Build;

public class DiagnosticsTests
{
    [Fact]
    public void Gcc_error_with_column() =>
        Assert.Equal(new CompilerDiagnostic("/p/a.c", 12, 5, true, "'x' undeclared (first use in this function)"),
            CompilerDiagnostic.TryParse("/p/a.c:12:5: error: 'x' undeclared (first use in this function)"));

    [Fact]
    public void Gcc_warning_and_fatal_error_without_column()
    {
        Assert.Equal(new CompilerDiagnostic("a.c", 3, null, false, "unused"), CompilerDiagnostic.TryParse("a.c:3: warning: unused"));
        Assert.Equal(new CompilerDiagnostic("/p/a.c", 1, 10, true, "Foo.h: No such file or directory"),
            CompilerDiagnostic.TryParse("/p/a.c:1:10: fatal error: Foo.h: No such file or directory\r\n"));
    }

    [Fact]
    public void Windows_drive_letter_paths_are_kept() =>
        Assert.Equal(new CompilerDiagnostic(@"C:\proj\t.c", 7, 1, true, "expected ';' before '}' token"),
            CompilerDiagnostic.TryParse(@"C:\proj\t.c:7:1: error: expected ';' before '}' token"));

    [Theory]
    [InlineData("/p/a.c: In function 'main':")]
    [InlineData("/p/a.c:5:3: note: declared here")]
    [InlineData("   12 |     foo(1);")]
    [InlineData("      |     ^~~")]
    [InlineData("/x/bin/../arm-none-eabi/bin/ld: /tmp/a.o: in function `main':")]
    [InlineData("/x/bin/ld: /x/lib/libc_nano.a(libc_a-closer.o): note: the message above does not take linker garbage collection into account")]
    [InlineData("In file included from /stubs/SetMotor.h:3,")]
    [InlineData("")]
    public void Context_lines_and_notes_are_not_diagnostics(string line) => Assert.Null(CompilerDiagnostic.TryParse(line));

    [Fact]
    public void Ld_undefined_reference_with_ld_prefix_and_line()
    {
        var d = CompilerDiagnostic.TryParse("/t/bin/../lib/gcc/arm-none-eabi/15.3.1/../../../../arm-none-eabi/bin/ld: /p/Test.c:9:(.text.startup.main+0x4): undefined reference to `Foo'");
        Assert.Equal(new CompilerDiagnostic("/p/Test.c", 9, null, true, "undefined reference to `Foo'"), d);
    }

    [Fact]
    public void Ld_messages_without_source_line_are_unlocated()
    {
        var w = CompilerDiagnostic.TryParse("closer.c:(.text._close_r+0xc): warning: _close is not implemented and will always fail");
        Assert.Equal(new CompilerDiagnostic("", 0, null, false, "closer.c:(.text._close_r+0xc): warning: _close is not implemented and will always fail"), w);

        Assert.Equal(new CompilerDiagnostic("", 0, null, true, "region `FLASH' overflowed by 120 bytes"),
            CompilerDiagnostic.TryParse("/t/bin/arm-none-eabi-ld: region `FLASH' overflowed by 120 bytes"));
        Assert.Equal(new CompilerDiagnostic("", 0, null, true, "ld returned 1 exit status"),
            CompilerDiagnostic.TryParse("collect2: error: ld returned 1 exit status"));
        Assert.Equal(new CompilerDiagnostic("", 0, null, true, "a.elf section `.init' will not fit in region `FLASH'"),
            CompilerDiagnostic.TryParse("C:/t/arm-none-eabi-ld.exe: a.elf section `.init' will not fit in region `FLASH'"));
    }

    [Fact]
    public void Localized_lines_still_parse()
    {
        var line = Builder.Localize("/p/a.c:4:2: error: too few arguments to function 'SetMotor'", UiLanguage.CHS);
        var d = CompilerDiagnostic.TryParse(line);
        Assert.NotNull(d);
        Assert.Equal(("/p/a.c", 4, true), (d!.File, d.Line, d.IsError));
        Assert.StartsWith("(该模块参数没有填写完整", d.Message);
    }

    [Fact]
    public void Parse_makes_relative_files_absolute_against_work_dir()
    {
        var work = Path.GetTempPath();
        var list = DiagnosticParser.Parse("a.c:1:1: error: x\nnoise\ncollect2: error: ld returned 1 exit status\n", work);
        Assert.Equal(2, list.Count);
        Assert.Equal(Path.GetFullPath("a.c", work), list[0].File);
        Assert.Equal("", list[1].File);
    }

    [Fact]
    public void Localize_applies_MainForm_hints_for_chinese_only()
    {
        const string log = "a.c: In function 'main':\n" +
                           "a.c:3:5: error: 'q' undeclared (first use in this function)\n" +
                           "a.c:3:5: note: each undeclared identifier is reported only once for each function it appears in\n" +
                           "a.c:4:1: error: expected ';' before '}' token\n" +
                           "a.c:1:10: fatal error: X.h: No such file or directory\n" +
                           "a.c:5: undefined reference to `F'\n" +
                           "a.c:6:1: error: too many arguments to function 'G'\n";
        Assert.Equal(log, Builder.Localize(log, UiLanguage.Default));

        var chs = Builder.Localize(log, UiLanguage.CHS);
        Assert.Equal(chs, Builder.Localize(log, UiLanguage.CHT));
        Assert.Equal(
            "a.c: 在函数中 'main':\n" +
            "a.c:3:5: error: 'q' 没有声明，请检查所使用的名称、大小写是否正确 undeclared (first use in this function)\n" +
            "a.c:3:5: note: 对于每个出现的函数，每个未声明的标识符只报告一次错误\n" +
            "a.c:4:1: error: 前面缺少;符号 '}' token\n" +
            "a.c:1:10: fatal error: X.h: 没有找到文件或目录 No such file or directory\n" +
            "a.c:5: 未定义子程序,可能缺少引用子程序文件 undefined reference to `F'\n" +
            "a.c:6:1: error: 函数调用时参数过多 too many arguments to function 'G'\n",
            chs);
    }
}
