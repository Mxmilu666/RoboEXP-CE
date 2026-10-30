using RoboExp.App.Localization;
using RoboExp.Core.Build;
using RoboExp.Core.Install;
using RoboExp.Core.Library;

namespace RoboExp.App.Services;

/// <summary>
/// Process-wide state resolved from <see cref="AppSettings"/>: vendor install, toolchain, language, robot type and
/// the robot's <see cref="SystemLibrary"/>. Reloaded when Options change; <see cref="Changed"/> tells views to refresh.
/// </summary>
public sealed class AppEnvironment
{
    public AppEnvironment(AppSettings settings, string settingsPath)
    {
        Settings = settings;
        SettingsPath = settingsPath;
        Reload();
    }

    public AppSettings Settings { get; }
    public string SettingsPath { get; }

    public VendorInstall? Install { get; private set; }
    public string? InstallError { get; private set; }
    public RobotType RobotType { get; private set; } = RobotType.Parse("E6RCU");
    public UiLanguage Language { get; private set; } = UiLanguage.CHS;
    public SystemLibrary? Library { get; private set; }
    public string? LibraryError { get; private set; }

    /// <summary>Raised after <see cref="Reload"/> or <see cref="SetRobotType"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>Workspace appearance changed without reloading robot libraries.</summary>
    public event EventHandler? AppearanceChanged;

    public void NotifyAppearanceChanged() => AppearanceChanged?.Invoke(this, EventArgs.Empty);

    public void Reload()
    {
        (Install, InstallError) = ResolveInstall(Settings.VendorPath);
        Language = Settings.EffectiveLanguage(Install);
        RobotType = Settings.EffectiveRobotType(Install);
        Localizer.Instance.Load();
        Localizer.Instance.SetLanguage(Language);
        LoadLibrary();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetRobotType(RobotType type)
    {
        if (type == RobotType && Library != null) return;
        RobotType = type;
        Settings.RobotType = type.Name;
        SaveSettings();
        LoadLibrary();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetLanguage(UiLanguage language)
    {
        Settings.Language = language;
        SaveSettings();
        Language = language;
        Localizer.Instance.SetLanguage(language);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SaveSettings() => Settings.Save(SettingsPath);

    /// <summary>The configured toolchain bin, else <see cref="ToolchainLocator.Default"/>. Throws with a readable message when none is usable.</summary>
    public ToolchainLocator ResolveToolchain() =>
        string.IsNullOrWhiteSpace(Settings.ToolchainBin) ? ToolchainLocator.Default : new ToolchainLocator(Settings.ToolchainBin);

    public static (VendorInstall? Install, string? Error) ResolveInstall(string? configuredPath)
    {
        try
        {
            return (string.IsNullOrWhiteSpace(configuredPath) ? VendorInstall.Locate() : new VendorInstall(configuredPath), null);
        }
        catch (DirectoryNotFoundException e) { return (null, e.Message); }
    }

    private void LoadLibrary()
    {
        Library = null;
        LibraryError = null;
        if (Install == null) { LibraryError = InstallError; return; }
        try { Library = SystemLibrary.Load(Install, RobotType); }
        catch (Exception e) when (e is IOException or System.Xml.XmlException or UnauthorizedAccessException or InvalidDataException)
        {
            LibraryError = e.Message;
        }
    }
}
