using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using RoboExp.Core.Install;
using RoboExp.Tests.Build;

namespace RoboExp.Tests.Setup;

public sealed class ToolchainInstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rx-tc-" + Guid.NewGuid().ToString("N")[..8]);

    public ToolchainInstallerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void CatalogHasAllHostsWithPinnedHashes()
    {
        Assert.Equal(
            ["aarch64", "darwin-arm64", "darwin-x86_64", "mingw-w64-x86_64", "x86_64"],
            ToolchainCatalog.All.Select(p => p.Host).Order(StringComparer.Ordinal));
        foreach (var p in ToolchainCatalog.All)
        {
            Assert.Matches("^[0-9a-f]{64}$", p.Sha256);
            Assert.StartsWith("https://", p.Url);
            Assert.EndsWith("/" + p.FileName, p.Url);
            Assert.True(p.Size > 100_000_000);
            Assert.Equal(p.Host.StartsWith("mingw") ? ArchiveKind.Zip : ArchiveKind.TarXz, p.Kind);
        }
        Assert.All(ToolchainCatalog.All.Where(p => p.Host != "darwin-x86_64"), p => Assert.Equal(ToolchainCatalog.Version, p.Version));
        Assert.Equal("arm-gnu-toolchain-15.3.rel2-darwin-arm64-arm-none-eabi",
            ToolchainCatalog.ForHost(OSPlatform.OSX, Architecture.Arm64)!.DirectoryName);
    }

    [Theory]
    [InlineData("OSX", Architecture.Arm64, "darwin-arm64")]
    [InlineData("OSX", Architecture.X64, "darwin-x86_64")]
    [InlineData("LINUX", Architecture.X64, "x86_64")]
    [InlineData("LINUX", Architecture.Arm64, "aarch64")]
    [InlineData("WINDOWS", Architecture.X64, "mingw-w64-x86_64")]
    [InlineData("WINDOWS", Architecture.Arm64, "mingw-w64-x86_64")]
    [InlineData("WINDOWS", Architecture.X86, null)]
    [InlineData("LINUX", Architecture.Arm, null)]
    [InlineData("FREEBSD", Architecture.X64, null)]
    public void ForHostMapsPlatforms(string os, Architecture arch, string? host)
    {
        Assert.Equal(host, ToolchainCatalog.ForHost(OSPlatform.Create(os), arch)?.Host);
    }

    [Fact]
    public void ForCurrentHostIsAvailableOnSupportedPlatforms()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
            Assert.NotNull(ToolchainCatalog.ForCurrentHost());
    }

    [Fact]
    public async Task ComputesSha256()
    {
        var file = Path.Combine(_dir, "abc");
        await File.WriteAllBytesAsync(file, "abc"u8.ToArray());
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            await ToolchainInstaller.ComputeSha256Async(file, CancellationToken.None));
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public async Task ExtractTarXzPreservesExecBitAndSymlinks()
    {
        var src = Path.Combine(_dir, "src", "top");
        Directory.CreateDirectory(Path.Combine(src, "bin"));
        var tool = Path.Combine(src, "bin", "tool");
        await File.WriteAllTextAsync(tool, "#!/bin/sh\necho hi\n");
        File.SetUnixFileMode(tool, (UnixFileMode)0b111_101_101);
        File.CreateSymbolicLink(Path.Combine(src, "bin", "alias"), "tool");
        var archive = Path.Combine(_dir, "a.tar.xz");
        Tar("-cJf", archive, "-C", Path.Combine(_dir, "src"), "top");

        var dest = Path.Combine(_dir, "out");
        await ToolchainInstaller.ExtractAsync(archive, ArchiveKind.TarXz, dest, CancellationToken.None);

        var extracted = Path.Combine(dest, "top", "bin");
        Assert.Equal((UnixFileMode)0b111_101_101, File.GetUnixFileMode(Path.Combine(extracted, "tool")) & (UnixFileMode)0b111_111_111);
        var link = new FileInfo(Path.Combine(extracted, "alias"));
        Assert.Equal("tool", link.LinkTarget);
        Assert.Equal("#!/bin/sh\necho hi\n", await File.ReadAllTextAsync(link.FullName));
    }

    [UnixFact]
    public async Task ExtractReportsTarFailure()
    {
        var bogus = Path.Combine(_dir, "bogus.tar.xz");
        await File.WriteAllTextAsync(bogus, "not an archive");
        await Assert.ThrowsAsync<IOException>(() =>
            ToolchainInstaller.ExtractAsync(bogus, ArchiveKind.TarXz, Path.Combine(_dir, "out"), CancellationToken.None));
    }

    [Fact]
    public async Task InstallDownloadsVerifiesAndExtracts()
    {
        var (pkg, bytes) = await FakePackageAsync();
        var root = Path.Combine(_dir, "toolchains");
        var handler = new BytesHandler(bytes);
        var phases = new List<string>();

        var bin = await new ToolchainInstaller(new HttpClient(handler))
            .InstallAsync(pkg, root, new SyncProgress(p => phases.Add(p.Phase)), CancellationToken.None);

        Assert.Equal(Path.Combine(root, pkg.DirectoryName, "bin"), bin);
        Assert.True(File.Exists(Path.Combine(bin, GccName)));
        Assert.Equal(1, handler.Requests);
        Assert.Equal(["download", "verify", "extract"], phases.Distinct());
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(root, ".download")));
        Assert.Equal([pkg.DirectoryName, ".download"], Directory.GetFileSystemEntries(root).Select(Path.GetFileName).OrderDescending());

        // Already installed: no second download.
        Assert.Equal(bin, await new ToolchainInstaller(new HttpClient(handler)).InstallAsync(pkg, root, null, CancellationToken.None));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task InstallReusesVerifiedDownload()
    {
        var (pkg, bytes) = await FakePackageAsync();
        var root = Path.Combine(_dir, "toolchains");
        Directory.CreateDirectory(Path.Combine(root, ".download"));
        await File.WriteAllBytesAsync(Path.Combine(root, ".download", pkg.FileName), bytes);
        var handler = new BytesHandler(bytes);

        var bin = await new ToolchainInstaller(new HttpClient(handler)).InstallAsync(pkg, root, null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(bin, GccName)));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task InstallRejectsHashMismatchAndDeletesDownload()
    {
        var (pkg, bytes) = await FakePackageAsync();
        pkg = pkg with { Sha256 = new string('0', 64) };
        var root = Path.Combine(_dir, "toolchains");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ToolchainInstaller(new HttpClient(new BytesHandler(bytes))).InstallAsync(pkg, root, null, CancellationToken.None));

        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(root, ".download")));
        Assert.False(Directory.Exists(Path.Combine(root, pkg.DirectoryName)));
    }

    [Fact]
    public async Task InstallHonorsCancellation()
    {
        var (pkg, _) = await FakePackageAsync();
        var root = Path.Combine(_dir, "toolchains");
        var big = RandomNumberGenerator.GetBytes(2 * 1024 * 1024);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ToolchainInstaller(new HttpClient(new BytesHandler(big)))
                .InstallAsync(pkg, root, new SyncProgress(p => { if (p.Phase == "download") cts.Cancel(); }), cts.Token));

        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(root, ".download")));
        Assert.False(Directory.Exists(Path.Combine(root, pkg.DirectoryName)));
    }

    private static string GccName => "arm-none-eabi-gcc" + (OperatingSystem.IsWindows() ? ".exe" : "");

    /// <summary>A tiny toolchain archive shaped like the real ones: tar.xz with a top directory (zip without one on Windows).</summary>
    private async Task<(ToolchainPackage Pkg, byte[] Bytes)> FakePackageAsync()
    {
        var kind = OperatingSystem.IsWindows() ? ArchiveKind.Zip : ArchiveKind.TarXz;
        var pkg = new ToolchainPackage("99.1.rel1", "testhost", "https://example.invalid/fake", "", 0, kind);
        var stage = Path.Combine(_dir, "stage");
        var top = Path.Combine(stage, pkg.DirectoryName);
        Directory.CreateDirectory(Path.Combine(top, "bin"));
        await File.WriteAllTextAsync(Path.Combine(top, "bin", GccName), "fake gcc");
        var archive = Path.Combine(_dir, pkg.FileName);
        if (kind == ArchiveKind.Zip) ZipFile.CreateFromDirectory(top, archive);
        else Tar("-cJf", archive, "-C", stage, pkg.DirectoryName);
        var bytes = await File.ReadAllBytesAsync(archive);
        return (pkg with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Size = bytes.Length }, bytes);
    }

    private static void Tar(params string[] args)
    {
        var psi = new ProcessStartInfo("tar") { RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err);
    }

    private sealed class BytesHandler(byte[] bytes) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(bytes)) });
        }
    }

    private sealed class SyncProgress(Action<ToolchainInstallProgress> report) : IProgress<ToolchainInstallProgress>
    {
        public void Report(ToolchainInstallProgress value) => report(value);
    }
}
