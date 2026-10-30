using RoboExp.Core.Build;

namespace RoboExp.Tests.Build;

public sealed class ToolchainLocatorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("rx-locator-").FullName;

    private string AppDirectory => Path.Combine(_root, "app");
    private string UserRoot => Path.Combine(_root, "user", "toolchains");
    private string HomeRoot => Path.Combine(_root, "home", ".local", "toolchains");
    private string PathBin => Path.Combine(_root, "path", "bin");
    private string BundledRoot => Path.Combine(AppDirectory, "toolchains");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Environment_override_wins_over_bundle(bool executablePath)
    {
        AddToolchain(BundledRoot, "15.3.rel2");
        var bin = AddCompiler(Path.Combine(_root, "override", "bin"));
        var result = Discover(executablePath ? Path.Combine(bin, "arm-none-eabi-gcc" + ToolchainLocator.ExeSuffix) : bin);

        Assert.Equal(bin, result.ArmNoneEabiBin);
        Assert.True(result.IsAvailable);
    }

    [Fact]
    public void Bundle_wins_over_newer_user_home_and_path_toolchains()
    {
        var bundled = AddToolchain(BundledRoot, "15.3.rel2");
        AddToolchain(UserRoot, "99.1.rel1");
        AddToolchain(HomeRoot, "99.2.rel1");
        AddCompiler(PathBin);

        var result = Discover();

        Assert.Equal(bundled, result.ArmNoneEabiBin);
        Assert.True(result.IsAvailable);
    }

    [Fact]
    public void Mac_bundle_resources_win_over_user_toolchain()
    {
        var bin = Path.Combine(_root, "RoboExp.app", "Contents", "MacOS");
        var bundled = AddToolchain(Path.Combine(_root, "RoboExp.app", "Contents", "Resources", "toolchains"), "15.3.rel2");
        AddToolchain(UserRoot, "99.1.rel1");
        var result = ToolchainLocator.Discover(bin, UserRoot, HomeRoot, null, PathBin);
        Assert.Equal(Path.GetFullPath(bundled), result.ArmNoneEabiBin);
    }

    [Fact]
    public void Newest_valid_bundled_toolchain_is_selected()
    {
        AddToolchain(BundledRoot, "9.3.rel1");
        AddToolchain(BundledRoot, "15.3.rel1");
        var newest = AddToolchain(BundledRoot, "15.3.rel2");
        Directory.CreateDirectory(Path.Combine(BundledRoot, "arm-gnu-toolchain-99.1.rel1-test", "bin"));

        Assert.Equal(newest, Discover().ArmNoneEabiBin);
    }

    [Fact]
    public void Missing_bundle_falls_back_to_user_then_home_then_path()
    {
        var user = AddToolchain(UserRoot, "15.3.rel2");
        var home = AddToolchain(HomeRoot, "16.1.rel1");
        AddCompiler(PathBin);

        Assert.Equal(user, Discover().ArmNoneEabiBin);
        Directory.Delete(UserRoot, recursive: true);
        Assert.Equal(home, Discover().ArmNoneEabiBin);
        Directory.Delete(HomeRoot, recursive: true);
        Assert.Equal(PathBin, Discover().ArmNoneEabiBin);
    }

    [Fact]
    public void Bundle_for_other_executable_suffix_is_ignored()
    {
        var bin = Path.Combine(BundledRoot, "arm-gnu-toolchain-15.3.rel2-test", "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "arm-none-eabi-gcc" + (OperatingSystem.IsWindows() ? "" : ".exe")), "");
        var user = AddToolchain(UserRoot, "14.1.rel1");

        Assert.Equal(user, Discover().ArmNoneEabiBin);
    }

    [Fact]
    public void No_compiler_returns_unavailable_locator()
    {
        var result = Discover();

        Assert.Equal("", result.ArmNoneEabiBin);
        Assert.False(result.IsAvailable);
    }

    private ToolchainLocator Discover(string? env = null) =>
        ToolchainLocator.Discover(AppDirectory, UserRoot, HomeRoot, env, PathBin);

    private static string AddToolchain(string root, string version) =>
        AddCompiler(Path.Combine(root, "arm-gnu-toolchain-" + version + "-test", "bin"));

    private static string AddCompiler(string bin)
    {
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "arm-none-eabi-gcc" + ToolchainLocator.ExeSuffix), "");
        return bin;
    }
}
