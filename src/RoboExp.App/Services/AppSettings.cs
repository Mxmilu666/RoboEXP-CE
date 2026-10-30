using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using RoboExp.Core.Install;
using RoboExp.Core.Library;

namespace RoboExp.App.Services;

public enum AppTheme { System, Light, Dark }
public enum FlowBackgroundMode { Default, Classic, Custom }

/// <summary>
/// User settings persisted as JSON under the user config dir (<c>$ROBOEXP_CONFIG_DIR</c> overrides).
/// Mirrors the parts of the original <c>ConfigInfo</c> + <c>Resources/SoftType.xml</c> (StartupType/Language) that apply.
/// Null paths mean "auto-detect" (<see cref="VendorInstall.Locate"/>, <c>ToolchainLocator.Default</c>).
/// </summary>
public sealed class AppSettings
{
    public const string ConfigDirEnvVar = "ROBOEXP_CONFIG_DIR";
    public const int MaxRecentFiles = 10;

    public string? VendorPath { get; set; }
    public string? ToolchainBin { get; set; }
    public UiLanguage? Language { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.System;
    public FlowBackgroundMode FlowBackground { get; set; } = FlowBackgroundMode.Default;
    public string FlowBackgroundColor { get; set; } = "#8080C0";
    public string? RobotType { get; set; }
    public bool CompileBeforeDownload { get; set; } = true;
    public bool ShowMessageAfterDownload { get; set; } = true;
    public List<string> RecentFiles { get; set; } = [];
    public PaneLayout Layout { get; set; } = new();

    public sealed class PaneLayout
    {
        public double LeftWidth { get; set; } = 220;
        public double RightWidth { get; set; } = 260;
        public double BottomHeight { get; set; } = 180;
        public bool IconsVisible { get; set; } = true;
        public bool RightVisible { get; set; } = true;
        public bool OutputVisible { get; set; } = true;
    }

    public static string DefaultDirectory
    {
        get
        {
            var env = Environment.GetEnvironmentVariable(ConfigDirEnvVar);
            return !string.IsNullOrEmpty(env)
                ? env
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RoboExpCE");
        }
    }

    public static string DefaultPath => Path.Combine(DefaultDirectory, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Missing or unreadable files yield defaults (a corrupt file must never block startup).</summary>
    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }

    public void AddRecentFile(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecentFiles) RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
    }

    /// <summary>Configured language, else the vendor <c>System</c> file's <c>&lt;Language&gt;</c>, else CHS.</summary>
    public UiLanguage EffectiveLanguage(VendorInstall? install)
    {
        if (Language is { } lang) return lang;
        if (install != null)
        {
            var value = ReadXmlValue(Path.Combine(install.Root, "System"), "Language");
            if (value != null) return ParseLanguage(value);
        }
        return UiLanguage.CHS;
    }

    /// <summary>Configured type, else vendor <c>SoftType.xml</c> <c>&lt;StartupType&gt;</c> when supported, else E6RCU.</summary>
    public RobotType EffectiveRobotType(VendorInstall? install)
    {
        foreach (var name in new[] { RobotType, install == null ? null : ReadXmlValue(Path.Combine(install.ResourcesDirectory, "SoftType.xml"), "StartupType") })
        {
            var match = Core.Install.RobotType.Supported.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return Core.Install.RobotType.Parse("E6RCU");
    }

    /// <summary>Original culture codes: "CHS", "CHT", anything else ("Default") = English.</summary>
    public static UiLanguage ParseLanguage(string value) => value.Trim().ToUpperInvariant() switch
    {
        "CHS" => UiLanguage.CHS,
        "CHT" => UiLanguage.CHT,
        _ => UiLanguage.Default,
    };

    private static string? ReadXmlValue(string path, string element)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return XDocument.Load(path).Root?.Element(element)?.Value;
        }
        catch (Exception e) when (e is System.Xml.XmlException or IOException) { return null; }
    }
}
