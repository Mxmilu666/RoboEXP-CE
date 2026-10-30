using RoboExp.Core.Build;
using RoboExp.Core.Install;
using RoboExp.Core.Library;
using RoboExp.Core.Model;

namespace RoboExp.Tests.Build;

/// <summary>
/// Drives <see cref="Builder"/> through a fake install (JMCompiler.xml + Include dir) and fake arm-none-eabi tools (shell scripts
/// that log their arguments), so the JMCompiler step semantics are checked without a real toolchain.
/// Uses EV5, which has no Robots directory in the real install, so the shared stub cache of real types is untouched.
/// </summary>
public sealed class BuilderPipelineTests : IDisposable
{
    private static readonly RobotType Ev5 = new(SoftwareType.EV5);
    private readonly string _root = Directory.CreateTempSubdirectory("rbx-fake-").FullName;
    private readonly string _bin;
    private readonly string _project;
    private readonly string _argsLog;

    public BuilderPipelineTests()
    {
        if (OperatingSystem.IsWindows()) { _bin = _project = _argsLog = ""; return; }
        Directory.CreateDirectory(Path.Combine(_root, "Compiler"));
        File.WriteAllText(Path.Combine(_root, "Compiler", "JMCompiler.xml"), """
            <root>
              <EV5>
                <main>
                  <cmd0>"?C\..\ARM\bin\arm-none-eabi-gcc" ?I -c "?P\?N.c" -o "?P\?N.o"</cmd0>
                  <cmd1>?T "?P\?N.o</cmd1>
                  <cmd2>"?C\..\ARM\bin\arm-none-eabi-gcc" -L"?C\..\Robots\EV5\Include\o" -T x.ld "?O" -o "?P\?N.elf"</cmd2>
                  <cmd3>"?C\..\ARM\Bin\arm-none-eabi-objcopy" -O binary "?P\?N.elf" "?P\?N.bin"</cmd3>
                  <cmd4>?D "?P\?N.o"</cmd4>
                  <cmd5>?D "?P\?N.elf"</cmd5>
                </main>
                <func>
                  <cmd0>"?C\..\ARM\bin\arm-none-eabi-gcc" ?I -c "?P\?N.c" -o "?P\?N.o"</cmd0>
                  <cmd1>?D "?P\?N.o"</cmd1>
                </func>
              </EV5>
            </root>
            """);
        var include = Path.Combine(_root, "Robots", "EV5", "Include");
        Directory.CreateDirectory(Path.Combine(include, "o"));
        File.WriteAllText(Path.Combine(include, "JMlib.xml"), "<root><Func><Name>SetMotor</Name></Func></root>");
        File.WriteAllText(Path.Combine(include, "jmlib.c"), "/* lib */");

        _bin = Path.Combine(_root, "toolchain", "bin");
        Directory.CreateDirectory(_bin);
        _project = Path.Combine(_root, "proj");
        Directory.CreateDirectory(_project);
        _argsLog = Path.Combine(_project, "args.log");
        // gcc: logs args, honours -o, fails when the source contains FAIL, "forgets" the object for NOOBJ.
        WriteTool("arm-none-eabi-gcc", """
            #!/bin/sh
            echo "gcc $*" >> args.log
            out=""; src=""; prev=""
            for a in "$@"; do
              [ "$prev" = "-o" ] && out="$a"
              case "$a" in *.c) src="$a";; esac
              prev="$a"
            done
            if [ -n "$src" ] && grep -q FAIL "$src"; then echo "$src:3:1: error: boom" >&2; exit 1; fi
            if [ -n "$src" ] && grep -q NOOBJ "$src"; then exit 0; fi
            echo obj > "$out"
            """);
        WriteTool("arm-none-eabi-objcopy", """
            #!/bin/sh
            echo "objcopy $*" >> args.log
            for a in "$@"; do last="$a"; done
            printf 'BIN' > "$last"
            """);
    }

    private void WriteTool(string name, string script)
    {
        var path = Path.Combine(_bin, name);
        File.WriteAllText(path, script.Replace("\r\n", "\n") + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        var cache = Path.Combine(VendorInstall.CacheDirectory, Ev5.Name);
        if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
    }

    private Builder NewBuilder() => new(new VendorInstall(_root), new ToolchainLocator(_bin));

    private string Source(string text)
    {
        var path = Path.Combine(_project, "test.c");
        File.WriteAllText(path, text);
        return path;
    }

    [UnixFact]
    public async Task Main_build_runs_steps_in_order_and_cleans_intermediates()
    {
        File.WriteAllText(Path.Combine(_project, "extra.o"), "stale");
        var result = await NewBuilder().BuildAsync(Source("int main(){}"), Ev5, BuildGroup.Main);

        Assert.True(result.Success, result.Log);
        Assert.Equal(Path.Combine(_project, "test.bin"), result.BinaryPath);
        Assert.Equal("BIN", File.ReadAllText(result.BinaryPath!));
        Assert.False(File.Exists(Path.Combine(_project, "test.o")));
        Assert.False(File.Exists(Path.Combine(_project, "test.elf")));
        Assert.Equal("", result.Log);

        var lines = File.ReadAllLines(_argsLog);
        Assert.Equal(3, lines.Length);
        var include = Path.Combine(_root, "Robots", "EV5", "Include");
        var stubs = SystemLibrary.StubDirectoryFor(Ev5);
        Assert.Equal($"gcc -fpermissive -I{include} -I{_project} -I{stubs} -c {_project}/test.c -o {_project}/test.o", lines[0]);
        // "?O" = every *.o of the work dir, sorted; link steps silence the binutils >= 2.39 RWX segment warning.
        Assert.Equal($"gcc -Wl,--no-warn-rwx-segments -L{include}/o -T x.ld {_project}/extra.o {_project}/test.o -o {_project}/test.elf", lines[1]);
        Assert.Equal($"objcopy -O binary {_project}/test.elf {_project}/test.bin", lines[2]);
        Assert.Equal(SystemLibrary.StubHeaderText("SetMotor", Ev5), File.ReadAllText(Path.Combine(stubs, "SetMotor.h")));
    }

    [UnixFact]
    public async Task Failing_step_stops_tools_but_still_deletes_and_reports_diagnostics()
    {
        var result = await NewBuilder().BuildAsync(Source("FAIL"), Ev5, BuildGroup.Main);

        Assert.False(result.Success);
        Assert.Null(result.BinaryPath);
        Assert.Single(File.ReadAllLines(_argsLog));
        var d = Assert.Single(result.Diagnostics);
        Assert.Equal(new CompilerDiagnostic(Path.Combine(_project, "test.c"), 3, 1, true, "boom"), d);
        Assert.Contains("error: boom", result.Log);
    }

    [UnixFact]
    public async Task T_step_stops_when_file_is_missing()
    {
        var result = await NewBuilder().BuildAsync(Source("NOOBJ"), Ev5, BuildGroup.Main);

        Assert.False(result.Success);
        Assert.Single(File.ReadAllLines(_argsLog));
        Assert.False(File.Exists(Path.Combine(_project, "test.bin")));
    }

    [UnixFact]
    public async Task Func_group_succeeds_without_binary()
    {
        var result = await NewBuilder().BuildAsync(Source("void f(){}"), Ev5, BuildGroup.Func);

        Assert.True(result.Success, result.Log);
        Assert.Null(result.BinaryPath);
        Assert.False(File.Exists(Path.Combine(_project, "test.o")));
    }

    [UnixFact]
    public async Task Missing_tool_is_an_error_diagnostic()
    {
        File.Delete(Path.Combine(_bin, "arm-none-eabi-objcopy"));
        var result = await NewBuilder().BuildAsync(Source("int main(){}"), Ev5, BuildGroup.Main);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.IsError && d.Message.Contains("arm-none-eabi-objcopy not found"));
        Assert.False(File.Exists(Path.Combine(_project, "test.o")));
    }

    [UnixFact]
    public void Toolchain_locator_rejects_directory_without_gcc() =>
        Assert.Throws<DirectoryNotFoundException>(() => new ToolchainLocator(_project));

    [UnixFact]
    public async Task Missing_source_fails_cleanly()
    {
        var result = await NewBuilder().BuildAsync(Path.Combine(_project, "nope.c"), Ev5, BuildGroup.Main);
        Assert.False(result.Success);
        Assert.Contains("source file not found", result.Log);
    }
}
