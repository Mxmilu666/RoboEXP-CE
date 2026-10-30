using Avalonia.Media.Imaging;
using RoboExp.App.Localization;

namespace RoboExp.App.Services;

/// <summary>
/// Original module artwork from built-in manifest payloads and library files. Toolbar/menu actions use FlatIcons.
/// </summary>
public static class VendorImages
{
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Flow-control icon by original <c>IconType</c> name, e.g. <c>CtrlIcon("BeginIf")</c>.</summary>
    public static Bitmap? CtrlIcon(string iconType) => Get("RoboExp.Resources.CtrlIcons." + iconType + ".ico");

    public static Bitmap? Get(string resourceName)
    {
        if (Cache.TryGetValue(resourceName, out var cached)) return cached;
        Bitmap? bitmap = null;
        if (LegacyResources.GetResource(resourceName) is { } bytes)
        {
            try { bitmap = new Bitmap(new MemoryStream(bytes)); }
            catch (Exception) { bitmap = null; } // undecodable image: show caption only
        }
        Cache[resourceName] = bitmap;
        return bitmap;
    }

    private static readonly Dictionary<string, Bitmap?> LibraryIcons = new(StringComparer.Ordinal);

    /// <summary>Original library module image, shared across palette and placement menus.</summary>
    public static Bitmap? FromFile(string? path)
    {
        if (path == null || !File.Exists(path)) return null;
        if (LibraryIcons.TryGetValue(path, out var cached)) return cached;
        Bitmap? bitmap;
        try { bitmap = new Bitmap(path); }
        catch (Exception) { bitmap = null; }
        LibraryIcons[path] = bitmap;
        return bitmap;
    }

}
