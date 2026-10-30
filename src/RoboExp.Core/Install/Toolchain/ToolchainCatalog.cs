using System.Runtime.InteropServices;

namespace RoboExp.Core.Install;

public enum ArchiveKind { TarXz, Zip }

/// <summary>One downloadable Arm GNU Toolchain (arm-none-eabi) archive for a host.</summary>
/// <param name="Version">Arm release, e.g. <c>15.3.rel2</c>.</param>
/// <param name="Host">Arm's host string, e.g. <c>darwin-arm64</c>, <c>x86_64</c> (Linux), <c>mingw-w64-x86_64</c>.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the archive (from Arm's <c>.sha256asc</c>).</param>
/// <param name="Size">Archive size in bytes.</param>
public sealed record ToolchainPackage(string Version, string Host, string Url, string Sha256, long Size, ArchiveKind Kind)
{
    /// <summary>Directory the toolchain is installed as, e.g. <c>arm-gnu-toolchain-15.3.rel2-darwin-arm64-arm-none-eabi</c>.
    /// The tar.xz archives contain it as their single top directory; the Windows zip has no top directory and is extracted into it.</summary>
    public string DirectoryName => $"arm-gnu-toolchain-{Version}-{Host}-arm-none-eabi";

    /// <summary>Archive file name as published by Arm.</summary>
    public string FileName => DirectoryName + (Kind == ArchiveKind.Zip ? ".zip" : ".tar.xz");
}

/// <summary>
/// Pinned Arm GNU Toolchain downloads (gitlab.arm.com generic package registry). Sizes and SHA-256 come from the
/// registry and the official <c>&lt;file&gt;.sha256asc</c> files. Arm stopped publishing Intel macOS hosts after
/// 14.2.rel1, so <c>darwin-x86_64</c> pins that release (GCC 14, needs the same <c>-fpermissive</c> adaptation as 15).
/// </summary>
public static class ToolchainCatalog
{
    public const string Version = "15.3.rel2";

    private const string Registry = "https://gitlab.arm.com/api/v4/projects/tooling%2Fgnu-toolchains-for-arm/packages/generic/gnu-toolchain/";

    public static IReadOnlyList<ToolchainPackage> All { get; } =
    [
        Create(Version, "darwin-arm64", "f011e4832046155f06408dda6b252b21b139a8159c8529802227371f06656c60", 143668236, ArchiveKind.TarXz),
        Create("14.2.rel1", "darwin-x86_64", "2d9e717dd4f7751d18936ae1365d25916534105ebcb7583039eff1092b824505", 145118692, ArchiveKind.TarXz),
        Create(Version, "x86_64", "463df9665c246fa13bab2102115bbf83440661b6eb70b44f3dfd4712ca30037a", 159710596, ArchiveKind.TarXz),
        Create(Version, "aarch64", "a0dc18893171d39dec82fd8d57eeab3da836fd3df4b61978db3eccbf4a6bb948", 153209084, ArchiveKind.TarXz),
        Create(Version, "mingw-w64-x86_64", "0daff4141d4f62ad1e9794ca8bd430ef730f07fd78f6d71d2ffdffc21db8cb9e", 289240072, ArchiveKind.Zip),
    ];

    /// <summary>Package for the running OS and its native architecture (an x64 process under Rosetta still gets darwin-arm64).</summary>
    public static ToolchainPackage? ForCurrentHost()
    {
        var os = OperatingSystem.IsMacOS() ? OSPlatform.OSX
            : OperatingSystem.IsLinux() ? OSPlatform.Linux
            : OperatingSystem.IsWindows() ? OSPlatform.Windows
            : OSPlatform.Create(RuntimeInformation.OSDescription);
        return ForHost(os, RuntimeInformation.OSArchitecture);
    }

    /// <summary>Package for a host, or null when Arm publishes none. Windows on Arm64 uses the x86_64 build (emulated).</summary>
    public static ToolchainPackage? ForHost(OSPlatform os, Architecture arch)
    {
        string? host = null;
        if (os == OSPlatform.OSX) host = arch switch { Architecture.Arm64 => "darwin-arm64", Architecture.X64 => "darwin-x86_64", _ => null };
        else if (os == OSPlatform.Linux) host = arch switch { Architecture.X64 => "x86_64", Architecture.Arm64 => "aarch64", _ => null };
        else if (os == OSPlatform.Windows) host = arch is Architecture.X64 or Architecture.Arm64 ? "mingw-w64-x86_64" : null;
        return host == null ? null : All.First(p => p.Host == host);
    }

    private static ToolchainPackage Create(string version, string host, string sha256, long size, ArchiveKind kind)
    {
        var file = $"arm-gnu-toolchain-{version}-{host}-arm-none-eabi" + (kind == ArchiveKind.Zip ? ".zip" : ".tar.xz");
        return new ToolchainPackage(version, host, Registry + version + "/" + file, sha256, size, kind);
    }
}
