using System.Security.Cryptography;
using RoboExp.Core.Install;
using System.IO.Compression;

if (args is not [var repository, var payload])
    throw new ArgumentException("Usage: BundleAssets <repository> <payload>");
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

