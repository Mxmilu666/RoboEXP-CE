using System.Resources;
using Avalonia.Media.Imaging;
using RoboExp.App.Localization;

namespace RoboExp.App.Services;

/// <summary>
/// Images stored in the original WinForms forms' resources (e.g. <c>RoboExp.PropertyFrmNew.resources</c> "pictureBox3.Image"):
/// BinaryFormatter Bitmaps whose "Data" member holds the encoded PNG/JPEG stream.
/// </summary>
public static class FormResourceImages
{
    private static readonly Dictionary<string, byte[]?> Cache = new(StringComparer.Ordinal);

    /// <summary>The encoded image bytes of <paramref name="key"/> in <c>RoboExp.&lt;form&gt;.resources</c>, or null.</summary>
    public static byte[]? GetBytes(string form, string key)
    {
        var id = form + "/" + key;
        lock (Cache)
        {
            if (Cache.TryGetValue(id, out var cached)) return cached;
            byte[]? result = null;
            try
            {
                if (LegacyResources.GetResource("RoboExp." + form + ".resources") is { } bytes)
                {
                    using var reader = new ResourceReader(new MemoryStream(bytes));
                    reader.GetResourceData(key, out _, out var data);
                    result = ExtractImage(data);
                }
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or BadImageFormatException or IOException)
            {
                result = null;
            }
            Cache[id] = result;
            return result;
        }
    }

    public static Bitmap? Get(string form, string key)
    {
        if (GetBytes(form, key) is not { } data) return null;
        try { return new Bitmap(new MemoryStream(data)); }
        catch (Exception) { return null; }
    }

    /// <summary>Extracts encoded bitmap data without executing BinaryFormatter objects, including original GIF step illustrations.</summary>
    internal static byte[]? ExtractImage(byte[] data)
    {
        var span = data.AsSpan();
        if (System.Formats.Nrbf.NrbfDecoder.StartsWithPayloadHeader(span))
        {
            var record = System.Formats.Nrbf.NrbfDecoder.Decode(new MemoryStream(data));
            if (record is System.Formats.Nrbf.ClassRecord bitmap &&
                bitmap.GetArrayRecord("Data") is System.Formats.Nrbf.SZArrayRecord<byte> pixels)
                return pixels.GetArray();
        }
        ReadOnlySpan<byte> png = [0x89, (byte)'P', (byte)'N', (byte)'G'];
        var start = span.IndexOf(png);
        if (start >= 0)
        {
            var end = span[start..].IndexOf("IEND"u8);
            return end < 0 ? null : span.Slice(start, end + 8).ToArray();
        }
        ReadOnlySpan<byte> soi = [0xFF, 0xD8, 0xFF];
        start = span.IndexOf(soi);
        if (start < 0) return null;
        ReadOnlySpan<byte> eoi = [0xFF, 0xD9];
        var last = span[start..].LastIndexOf(eoi);
        return last < 0 ? null : span.Slice(start, last + 2).ToArray();
    }
}
