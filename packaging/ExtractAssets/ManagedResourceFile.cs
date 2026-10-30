using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Resources;
using System.Text;

namespace RoboExp.App.Localization;

/// <summary>
/// Reads embedded manifest resources from a .NET assembly file (the vendor <c>RoboExp.exe</c> and its
/// <c>zh-CHS</c>/<c>zh-CHT</c> satellite <c>RoboExp.resources.dll</c>) without loading or executing it.
/// </summary>
public sealed class ManagedResourceFile
{
    private readonly Dictionary<string, byte[]> _resources;

    private ManagedResourceFile(Dictionary<string, byte[]> resources) => _resources = resources;

    public IEnumerable<string> ResourceNames => _resources.Keys;

    public static ManagedResourceFile Open(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream, PEStreamOptions.PrefetchEntireImage);
        if (!pe.HasMetadata) throw new BadImageFormatException("Not a .NET assembly", path);
        var md = pe.GetMetadataReader();
        var dir = pe.PEHeaders.CorHeader!.ResourcesDirectory;
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (dir.Size == 0) return new ManagedResourceFile(result);
        var section = pe.GetSectionData(dir.RelativeVirtualAddress).GetContent();
        foreach (var handle in md.ManifestResources)
        {
            var res = md.GetManifestResource(handle);
            if (!res.Implementation.IsNil) continue; // linked/external resource
            var offset = checked((int)res.Offset);
            var length = BitConverter.ToInt32(section.AsSpan(offset, 4));
            result[md.GetString(res.Name)] = section.AsSpan(offset + 4, length).ToArray();
        }
        return new ManagedResourceFile(result);
    }

    public byte[]? GetResource(string name) => _resources.GetValueOrDefault(name);

    /// <summary>
    /// String entries of the named <c>.resources</c> table (e.g. <c>RoboExp.Sanway.CwLocalization.resources</c>).
    /// Non-string entries (bitmaps, sizes) are skipped; decoding them would need BinaryFormatter.
    /// </summary>
    public IReadOnlyDictionary<string, string> ReadStringTable(string resourceName)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        var bytes = GetResource(resourceName);
        if (bytes == null) return table;
        using var reader = new ResourceReader(new MemoryStream(bytes, writable: false));
        var names = new List<string>();
        var e = reader.GetEnumerator();
        while (e.MoveNext()) names.Add((string)e.Key);
        foreach (var name in names)
        {
            reader.GetResourceData(name, out var type, out var data);
            if (type != "ResourceTypeCode.String") continue;
            using var br = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
            table[name] = br.ReadString();
        }
        return table;
    }
}
