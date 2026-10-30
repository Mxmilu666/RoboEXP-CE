using System.Resources;
using System.Text;
using Avalonia.Platform;

namespace RoboExp.App.Localization;

/// <summary>Original manifest payloads embedded as data, without loading vendor assemblies.</summary>
public static class LegacyResources
{
    private static readonly Dictionary<string, byte[]?> Cache = new(StringComparer.Ordinal);

    public static byte[]? GetResource(string name, string culture = "neutral")
    {
        var path = culture + "/" + name;
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached)) return cached;
            var uri = new Uri("avares://RoboExp.App/Assets/LegacyResources/" + path);
            byte[]? bytes = null;
            if (AssetLoader.Exists(uri))
            {
                using var stream = AssetLoader.Open(uri);
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
            }
            Cache[path] = bytes;
            return bytes;
        }
    }

    /// <summary>Reads only strings; serialized WinForms objects are never instantiated.</summary>
    public static IReadOnlyDictionary<string, string> ReadStringTable(string name, string culture = "neutral")
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        if (GetResource(name, culture) is not { } bytes) return table;
        using var reader = new ResourceReader(new MemoryStream(bytes, writable: false));
        var entries = reader.GetEnumerator();
        while (entries.MoveNext())
        {
            var key = (string)entries.Key;
            reader.GetResourceData(key, out var type, out var data);
            if (type != "ResourceTypeCode.String") continue;
            using var value = new BinaryReader(new MemoryStream(data, writable: false), Encoding.UTF8);
            table[key] = value.ReadString();
        }
        return table;
    }
}
