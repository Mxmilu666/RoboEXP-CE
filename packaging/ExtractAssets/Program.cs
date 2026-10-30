using System.IO.Compression;
using System.Security.Cryptography;
using RoboExp.App.Localization;

if (args is not [var source, var repository])
    throw new ArgumentException("Usage: ExtractAssets <extracted-original-directory> <repository>");
source = Path.GetFullPath(source);
repository = Path.GetFullPath(repository);
var ui = Path.Combine(repository, "src", "RoboExp.App", "Assets", "LegacyResources");
foreach (var (culture, assembly) in new[] {
    ("neutral", "RoboExp.exe"), ("CHS", "zh-CHS/RoboExp.resources.dll"), ("CHT", "zh-CHT/RoboExp.resources.dll") })
{
    var resourceFile = ManagedResourceFile.Open(Path.Combine(source, assembly));
    var directory = Path.Combine(ui, culture);
    Directory.CreateDirectory(directory);
    foreach (var name in resourceFile.ResourceNames)
    {
        if (Path.GetFileName(name) != name) throw new InvalidDataException($"Unexpected resource name {name}");
        File.WriteAllBytes(Path.Combine(directory, name), resourceFile.GetResource(name)!);
    }
    Console.WriteLine($"Extracted {culture}: {resourceFile.ResourceNames.Count()} manifest resources");
}
var assets = Path.Combine(repository, "assets");
Directory.CreateDirectory(assets);
var archive = Path.Combine(assets, "robot-resources.zip");
using (var zip = new ZipArchive(File.Create(archive), ZipArchiveMode.Create))
{
    foreach (var directory in new[] { "Robots", "Resources", "Icons", "Help", "External Tools" })
    foreach (var file in Directory.EnumerateFiles(Path.Combine(source, directory), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        if (new[] { ".exe", ".dll", ".pdb" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
        Add(file);
    }
    foreach (var name in new[] { "System", "BaseKey.xml", "Compiler/JMCompiler.xml" }) Add(Path.Combine(source, name));
    void Add(string file)
    {
        var entry = zip.CreateEntry(Path.GetRelativePath(source, file).Replace('\\', '/'), CompressionLevel.SmallestSize);
        entry.LastWriteTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var input = File.OpenRead(file);
        using var output = entry.Open();
        input.CopyTo(output);
    }
}
await using var stream = File.OpenRead(archive);
var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
File.WriteAllText(archive + ".sha256", $"{hash}  robot-resources.zip\n");
Console.WriteLine($"Robot data archive: {new FileInfo(archive).Length:N0} bytes; SHA-256 {hash}");
