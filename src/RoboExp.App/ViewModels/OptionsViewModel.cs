using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoboExp.App.Localization;
using RoboExp.App.Services;
using RoboExp.Core.Build;
using RoboExp.Core.Install;
using RoboExp.Core.Library;

namespace RoboExp.App.ViewModels;

/// <summary>
/// Tool → Options (original Options form): software type + language (SoftType.xml), download flags (ConfigInfo),
/// and the port's paths (vendor install, toolchain). Applied on OK only; unlike the original, changes apply immediately.
/// </summary>
public sealed partial class OptionsViewModel : ObservableObject
{
    private readonly AppEnvironment _env;
    private readonly IDialogService _dialogs;
    private readonly Dictionary<string, XElement> _softTypeNodes = new(StringComparer.OrdinalIgnoreCase);

    public OptionsViewModel(AppEnvironment env, IDialogService dialogs)
    {
        _env = env;
        _dialogs = dialogs;
        var s = env.Settings;
        _vendorPath = s.VendorPath ?? "";
        _toolchainBin = s.ToolchainBin ?? "";
        _compileBeforeDownload = s.CompileBeforeDownload;
        _showMessageAfterDownload = s.ShowMessageAfterDownload;
        _selectedRobotType = env.RobotType;
        _languageIndex = env.Language switch { UiLanguage.CHS => 1, UiLanguage.CHT => 2, _ => 0 };
        _themeIndex = (int)s.Theme;
        LoadSoftTypes(env.Install);
        UpdateDetected();
    }

    public IReadOnlyList<RobotType> RobotTypes => RobotType.Supported;

    /// <summary>Original comboBoxLanguage order: English, Simplified Chinese, Traditional Chinese.</summary>
    public IReadOnlyList<string> Languages =>
    [
        Localizer.Instance.T("Options:comboBoxLanguage.Items"),
        Localizer.Instance.T("Options:comboBoxLanguage.Items1"),
        Localizer.Instance.T("Options:comboBoxLanguage.Items2"),
    ];

    public IReadOnlyList<string> ThemeModes =>
    [
        Localizer.Instance.T("Port:Theme.System"),
        Localizer.Instance.T("Port:Theme.Light"),
        Localizer.Instance.T("Port:Theme.Dark"),
    ];

    [ObservableProperty]
    private int _themeIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RobotTypeDescription))]
    private RobotType _selectedRobotType;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RobotTypeDescription))]
    private int _languageIndex;

    [ObservableProperty]
    private bool _compileBeforeDownload;

    [ObservableProperty]
    private bool _showMessageAfterDownload;

    [ObservableProperty]
    private string _vendorPath;

    [ObservableProperty]
    private string _toolchainBin;

    [ObservableProperty]
    private string _detectedVendor = "";

    [ObservableProperty]
    private string _detectedToolchain = "";

    /// <summary>Set when OK was pressed and the settings were applied.</summary>
    public bool Accepted { get; private set; }

    public event EventHandler? CloseRequested;

    private UiLanguage SelectedLanguage => LanguageIndex switch { 1 => UiLanguage.CHS, 2 => UiLanguage.CHT, _ => UiLanguage.Default };

    /// <summary>Original UpdateRCUTypeDes: SoftType.xml &lt;Type&gt;&lt;Description&gt;&lt;Default|CHS|CHT&gt; for the selected language.</summary>
    public string RobotTypeDescription
    {
        get
        {
            if (!_softTypeNodes.TryGetValue(SelectedRobotType.Name, out var node)) return "";
            var description = node.Element("Description");
            var culture = SelectedLanguage switch { UiLanguage.CHS => "CHS", UiLanguage.CHT => "CHT", _ => "Default" };
            return description?.Element(culture)?.Value ?? "";
        }
    }

    partial void OnVendorPathChanged(string value) => UpdateDetected();
    partial void OnToolchainBinChanged(string value) => UpdateDetected();

    private void LoadSoftTypes(VendorInstall? install)
    {
        if (install == null) return;
        var path = Path.Combine(install.ResourcesDirectory, "SoftType.xml");
        try
        {
            if (XDocument.Load(path).Root?.Element("SoftType") is { } root)
                foreach (var e in root.Elements()) _softTypeNodes[e.Name.LocalName] = e;
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException) { }
    }

    private void UpdateDetected()
    {
        var loc = Localizer.Instance;
        var (install, error) = AppEnvironment.ResolveInstall(string.IsNullOrWhiteSpace(VendorPath) ? null : VendorPath);
        DetectedVendor = install != null ? loc.T("Port:Detected") + install.Root : error ?? loc.T("Port:NotFound");
        string toolchain;
        try
        {
            var locator = string.IsNullOrWhiteSpace(ToolchainBin) ? ToolchainLocator.Default : new ToolchainLocator(ToolchainBin);
            toolchain = locator.IsAvailable ? loc.T("Port:Detected") + locator.ArmNoneEabiBin : loc.T("Port:NotFound");
        }
        catch (DirectoryNotFoundException e) { toolchain = e.Message; }
        DetectedToolchain = toolchain;
    }

    [RelayCommand]
    private async Task BrowseVendor()
    {
        var dir = await _dialogs.PickFolderAsync(Localizer.Instance.T("Port:VendorPath"), NullIfEmpty(VendorPath));
        if (dir != null) VendorPath = dir;
    }

    [RelayCommand]
    private async Task BrowseToolchain()
    {
        var dir = await _dialogs.PickFolderAsync(Localizer.Instance.T("Port:ToolchainBin"), NullIfEmpty(ToolchainBin));
        if (dir != null) ToolchainBin = dir;
    }

    /// <summary>Raised by the Setup Wizard button; the window runs the wizard over <see cref="Env"/>, then calls <see cref="ReloadPaths"/>.</summary>
    public event EventHandler? SetupWizardRequested;

    public AppEnvironment Env => _env;

    [RelayCommand]
    private void OpenSetupWizard() => SetupWizardRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Picks up paths the wizard stored in the settings.</summary>
    public void ReloadPaths()
    {
        VendorPath = _env.Settings.VendorPath ?? "";
        ToolchainBin = _env.Settings.ToolchainBin ?? "";
    }

    [RelayCommand]
    private void Ok()
    {
        var s = _env.Settings;
        var pathsChanged = NullIfEmpty(VendorPath) != s.VendorPath || NullIfEmpty(ToolchainBin) != s.ToolchainBin;
        s.VendorPath = NullIfEmpty(VendorPath);
        s.ToolchainBin = NullIfEmpty(ToolchainBin);
        s.CompileBeforeDownload = CompileBeforeDownload;
        s.ShowMessageAfterDownload = ShowMessageAfterDownload;
        s.Language = SelectedLanguage;
        s.RobotType = SelectedRobotType.Name;
        s.Theme = (AppTheme)ThemeIndex;
        _env.SaveSettings();
        ThemeService.Apply(s.Theme);
        if (pathsChanged || SelectedLanguage != _env.Language) _env.Reload();
        if (SelectedRobotType != _env.RobotType) _env.SetRobotType(SelectedRobotType);
        Accepted = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
