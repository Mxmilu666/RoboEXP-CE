using System.Runtime.InteropServices;
using System.Security.Cryptography;
using RoboExp.Core.Install;
using System.IO.Compression;

if (args is not [var repository, var payload, var rid])
    throw new ArgumentException("Usage: BundleAssets <repository> <payload> <runtime identifier>");
repository = Path.GetFullPath(repository);
payload = Path.GetFullPath(payload);
var archivePath = Path.Combine(repository, "assets", "robot-resources.zip");
var expectedHash = File.ReadAllText(archivePath + ".sha256").Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
await using (var input = File.OpenRead(archivePath))
{
    var actualHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input));
    if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Robot resource archive SHA-256 mismatch");
}
var vendor = Path.Combine(payload, "vendor", "app");
Directory.CreateDirectory(vendor);
await ZipFile.ExtractToDirectoryAsync(archivePath, vendor, overwriteFiles: true, CancellationToken.None);
foreach (var file in Directory.EnumerateFiles(vendor, "*", SearchOption.AllDirectories))
    if (Path.GetExtension(file).Equals(".exe", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(file).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException($"Original executable found in robot resources: {file}");
var install = new VendorInstall(vendor);
foreach (var type in RobotType.Supported) _ = install.IncludeDirectory(type);
Console.WriteLine($"Bundled vendor assets: {Directory.EnumerateFiles(vendor, "*", SearchOption.AllDirectories).Count()} files");

var (os, arch) = rid switch
{
    "win-arm64" => (OSPlatform.Windows, Architecture.Arm64),
    "win-x64" => (OSPlatform.Windows, Architecture.X64),
    "linux-arm64" => (OSPlatform.Linux, Architecture.Arm64),
    "linux-x64" => (OSPlatform.Linux, Architecture.X64),
    "osx-arm64" => (OSPlatform.OSX, Architecture.Arm64),
    "osx-x64" => (OSPlatform.OSX, Architecture.X64),
    _ => throw new ArgumentException($"Unsupported bundle runtime: {rid}"),
};
var package = ToolchainCatalog.ForHost(os, arch)!;
var cache = Path.Combine(repository, "dist", ".bundle-cache");
Directory.CreateDirectory(cache);
var archive = Path.Combine(cache, package.FileName);
if (!File.Exists(archive) || !await MatchesPackage(archive))
{
    // Arm's registry requires TLS 1.3; use the same macOS backend as ToolchainInstaller.
    const string networkFramework = "System.Net.Security.UseNetworkFramework";
    if (OperatingSystem.IsMacOS() && !AppContext.TryGetSwitch(networkFramework, out _)
        && Environment.GetEnvironmentVariable("DOTNET_SYSTEM_NET_SECURITY_USENETWORKFRAMEWORK") == null)
        AppContext.SetSwitch(networkFramework, true);
    using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    using var response = await client.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();
    var part = archive + ".part";
    Console.WriteLine($"Downloading build-time toolchain: {package.Url}");
    await using (var output = File.Create(part))
        await response.Content.CopyToAsync(output);
    if (!await MatchesPackage(part)) throw new InvalidDataException("Toolchain archive SHA-256/size mismatch");
    File.Move(part, archive, overwrite: true);
}
var toolchain = Path.Combine(payload, "toolchains", package.DirectoryName);
var extracted = Path.Combine(cache, ".extract-" + Guid.NewGuid().ToString("N"));
try
{
    await ToolchainInstaller.ExtractAsync(archive, package.Kind, extracted, CancellationToken.None);
    var content = package.Kind == ArchiveKind.Zip ? extracted : Path.Combine(extracted, package.DirectoryName);
    var suffix = os == OSPlatform.Windows ? ".exe" : "";
    foreach (var name in new[] { "arm-none-eabi-gcc", "arm-none-eabi-objcopy", "arm-none-eabi-size" })
        if (!File.Exists(Path.Combine(content, "bin", name + suffix)))
            throw new InvalidDataException($"Bundled toolchain is missing bin/{name}{suffix}");
    Directory.CreateDirectory(Path.GetDirectoryName(toolchain)!);
    if (Directory.Exists(toolchain)) Directory.Delete(toolchain, recursive: true);
    Directory.Move(content, toolchain);
}
finally
{
    if (Directory.Exists(extracted)) Directory.Delete(extracted, recursive: true);
}
Console.WriteLine($"Bundled toolchain: {package.DirectoryName}; SHA-256 verified ({package.Sha256})");

async Task<bool> MatchesPackage(string path)
{
    if (new FileInfo(path).Length != package.Size) return false;
    await using var stream = File.OpenRead(path);
    var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    return hash == package.Sha256;
}

