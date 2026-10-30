using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoboExp.App.Localization;
using RoboExp.App.Services;
using RoboExp.Core.Build;
using RoboExp.Core.Install;
using RoboExp.Core.Install.Inno;
using RoboExp.Core.Library;

namespace RoboExp.App.Setup;

public enum SetupStep { Welcome, Vendor, Toolchain, Finish }

/// <summary>Window services the wizard needs (implemented by <see cref="SetupWizardWindow"/>).</summary>
public interface ISetupWizardHost
{
    Task<string?> PickInstallerAsync(string title, string filterName);
    Task<string?> PickFolderAsync(string title);
    void Close();
}

/// <summary>
/// First-run setup (also reachable from Tool → Options → Paths): import the user's original RoboEXP
/// (installer .exe via <see cref="VendorImporter"/>, or an extracted directory) and install/choose the Arm GNU
/// toolchain (<see cref="ToolchainInstaller"/>). Finishing stores <see cref="AppSettings.VendorPath"/> and
/// <see cref="AppSettings.ToolchainBin"/> and reloads the <see cref="AppEnvironment"/>.
/// </summary>
public sealed partial class SetupWizardViewModel : ObservableObject
{
    private readonly AppEnvironment _env;
    private readonly ISetupWizardHost _host;
    private CancellationTokenSource? _operation;
    private Func<string> _vendorStatus;
    private Func<string> _toolchainStatus;
    private Func<string> _progressText = () => "";

    public SetupWizardViewModel(AppEnvironment env, ISetupWizardHost host)
    {
        _env = env;
        _host = host;
        _languageIndex = env.Language switch { UiLanguage.CHS => 1, UiLanguage.CHT => 2, _ => 0 };
        _vendorRoot = env.Install?.Root;
        try
        {
            var locator = env.ResolveToolchain();
            if (locator.IsAvailable) _toolchainBin = locator.ArmNoneEabiBin;
        }
        catch (DirectoryNotFoundException) { }
        Package = ToolchainCatalog.ForCurrentHost();
        _vendorStatus = DefaultVendorStatus;
        _toolchainStatus = DefaultToolchainStatus;
        Localizer.Instance.PropertyChanged += OnLocalizerChanged;
        RefreshTexts();
    }

    private void OnLocalizerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RefreshTexts();

    /// <summary>Robot resources are needed to open projects; the compiler can be installed on first compilation.</summary>
    public static bool IsSetupNeeded(AppEnvironment env) => env.Install == null;

    public ToolchainPackage? Package { get; }

    /// <summary>English, Simplified Chinese, Traditional Chinese (fixed native names: vendor resources may be absent).</summary>
    public IReadOnlyList<string> Languages { get; } = ["English", "简体中文", "繁體中文"];

    [ObservableProperty] private int _languageIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome), nameof(IsVendor), nameof(IsToolchain), nameof(IsFinish), nameof(CanGoBack), nameof(CanGoNext), nameof(StepIndex))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand), nameof(NextCommand))]
    private SetupStep _step;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VendorOk), nameof(IsComplete))]
    private string? _vendorRoot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolchainOk), nameof(IsComplete))]
    private string? _toolchainBin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanGoBack), nameof(CanGoNext))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand), nameof(NextCommand), nameof(FinishCommand), nameof(ImportInstallerCommand),
        nameof(ChooseVendorFolderCommand), nameof(UseVendorPathCommand), nameof(DownloadToolchainCommand), nameof(ChooseToolchainCommand),
        nameof(UseToolchainPathCommand))]
    private bool _isBusy;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _progressIndeterminate;
    [ObservableProperty] private string _progressLabel = "";
    [ObservableProperty] private string _vendorStatusText = "";
    [ObservableProperty] private string _toolchainStatusText = "";
    [ObservableProperty] private string _downloadLabel = "";
    [ObservableProperty] private string _summary = "";

    /// <summary>Typed/pasted vendor path: an installer .exe or an extracted install folder.</summary>
    [ObservableProperty] private string _vendorPathInput = "";

    /// <summary>Typed/pasted toolchain bin folder.</summary>
    [ObservableProperty] private string _toolchainPathInput = "";

    public bool IsWelcome => Step == SetupStep.Welcome;
    public bool IsVendor => Step == SetupStep.Vendor;
    public bool IsToolchain => Step == SetupStep.Toolchain;
    public bool IsFinish => Step == SetupStep.Finish;
    public int StepIndex => (int)Step;
    public bool IsIdle => !IsBusy;
    public bool VendorOk => VendorRoot != null;
    public bool ToolchainOk => ToolchainBin != null;
    public bool IsComplete => VendorOk && ToolchainOk;
    public bool HasPackage => Package != null;
    public bool CanGoBack => !IsBusy && Step != SetupStep.Welcome;
    public bool CanGoNext => !IsBusy && Step != SetupStep.Finish;

    /// <summary>Set once Finish applied the settings.</summary>
    public bool Completed { get; private set; }

    partial void OnLanguageIndexChanged(int value)
    {
        var language = value switch { 1 => UiLanguage.CHS, 2 => UiLanguage.CHT, _ => UiLanguage.Default };
        if (language != _env.Language) _env.SetLanguage(language);
    }

    partial void OnVendorRootChanged(string? value) => RefreshTexts();
    partial void OnToolchainBinChanged(string? value) => RefreshTexts();

    private static string T(string key) => Localizer.Instance.T("Port:" + key);
    private static string F(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);

    private string DefaultVendorStatus() => VendorRoot != null ? F("Setup.VendorOk", VendorRoot) : T("Setup.VendorNone");
    private string DefaultToolchainStatus() => ToolchainBin != null ? F("Setup.ToolchainOk", ToolchainBin) : T("Setup.ToolchainNone");

    private void RefreshTexts()
    {
        VendorStatusText = _vendorStatus();
        DownloadLabel = Package != null
            ? string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Port:Setup.Download"], Package.Version, Package.Size / 1e6)
            : "";
        ToolchainStatusText = _toolchainStatus();
        ProgressLabel = _progressText();
        Summary = (VendorRoot != null ? F("Setup.VendorOk", VendorRoot) : T("Setup.VendorNone")) + "\n"
            + (ToolchainBin != null ? F("Setup.ToolchainOk", ToolchainBin) : T("Setup.ToolchainNone"))
            + (IsComplete ? "" : "\n\n" + T("Setup.FinishIncomplete"));
    }

    private void SetProgress(Func<string> text, double value, bool indeterminate)
    {
        _progressText = text;
        Progress = value;
        ProgressIndeterminate = indeterminate;
        ProgressLabel = text();
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => Step = Step - 1;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next() => Step = Step + 1;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ImportInstaller()
    {
        var file = await _host.PickInstallerAsync(T("Setup.ImportInstaller"), T("Setup.InstallerFilter"));
        if (file != null) await ImportInstallerFromAsync(file);
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ChooseVendorFolder()
    {
        var dir = await _host.PickFolderAsync(T("Setup.ChooseVendorFolder"));
        if (dir != null) UseVendorDirectory(dir);
    }

    /// <summary>Uses <see cref="VendorPathInput"/>: a file is imported as an installer, a directory used as an install.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task UseVendorPath()
    {
        var path = VendorPathInput.Trim().Trim('"');
        if (path.Length == 0) return;
        if (File.Exists(path)) await ImportInstallerFromAsync(path);
        else UseVendorDirectory(path);
    }

    private async Task ImportInstallerFromAsync(string file)
    {
        VendorPathInput = file;
        await RunAsync(async ct =>
        {
            SetProgress(() => T("Setup.Reading"), 0, true);
            var progress = new Progress<VendorImportProgress>(p =>
            {
                if (p.Phase == "extract" && p.Total > 0)
                    SetProgress(() => F("Setup.Extracting", p.Done / 1e6, p.Total / 1e6), 100.0 * p.Done / p.Total, false);
                else if (p.Phase == "extract")
                    SetProgress(() => T("Setup.ExtractingUnknown"), 0, true);
                else if (p.Phase == "validate")
                    SetProgress(() => T("Setup.Validating"), 100, false);
            });
            var result = await VendorImporter.ImportInstallerAsync(file, null, new ThrottledProgress<VendorImportProgress>(progress), ct);
            VendorRoot = result.InstallRoot;
            _vendorStatus = result.Method == VendorImportMethod.Innoextract
                ? () => F("Setup.ImportedInnoextract", result.Version) + "\n" + DefaultVendorStatus()
                : () => F("Setup.Imported", result.FileCount, result.Version) + "\n" + DefaultVendorStatus();
        }, e => _vendorStatus = () => F("Setup.ImportFailed", e.Message));
    }

    private void UseVendorDirectory(string dir)
    {
        VendorPathInput = dir;
        try
        {
            VendorRoot = VendorImporter.ImportDirectory(dir).InstallRoot;
            _vendorStatus = DefaultVendorStatus;
        }
        catch (DirectoryNotFoundException e) { _vendorStatus = () => F("Setup.ImportFailed", e.Message); }
        RefreshTexts();
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task DownloadToolchain()
    {
        if (Package == null) return;
        await RunAsync(async ct =>
        {
            SetProgress(() => F("Setup.Downloading", 0.0, Package.Size / 1e6), 0, false);
            var progress = new Progress<ToolchainInstallProgress>(p =>
            {
                switch (p.Phase)
                {
                    case "download":
                        var total = p.Total > 0 ? p.Total : Package.Size;
                        SetProgress(() => F("Setup.Downloading", p.Done / 1e6, total / 1e6), 100.0 * p.Done / total, false);
                        break;
                    case "verify":
                        SetProgress(() => T("Setup.Verifying"), 0, true);
                        break;
                    case "prune":
                        SetProgress(() => T("Toolchain.Slimming"), 0, true);
                        break;
                    default:
                        SetProgress(() => T("Setup.ExtractingUnknown"), 0, true);
                        break;
                }
            });
            var bin = await new ToolchainInstaller().InstallAsync(Package, null, new ThrottledProgress<ToolchainInstallProgress>(progress), ct);
            ToolchainBin = new ToolchainLocator(bin).ArmNoneEabiBin;
            _toolchainStatus = DefaultToolchainStatus;
        }, e => _toolchainStatus = () => F("Setup.DownloadFailed", e.Message));
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ChooseToolchain()
    {
        var dir = await _host.PickFolderAsync(T("Setup.ChooseToolchain"));
        if (dir != null) UseToolchainDirectory(dir);
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void UseToolchainPath()
    {
        var path = ToolchainPathInput.Trim().Trim('"');
        if (path.Length > 0) UseToolchainDirectory(path);
    }

    private void UseToolchainDirectory(string dir)
    {
        ToolchainPathInput = dir;
        try
        {
            ToolchainBin = new ToolchainLocator(dir).ArmNoneEabiBin;
            _toolchainStatus = DefaultToolchainStatus;
        }
        catch (DirectoryNotFoundException e) { _toolchainStatus = () => F("Setup.DownloadFailed", e.Message); }
        RefreshTexts();
    }

    [RelayCommand]
    private void CancelOperation() => _operation?.Cancel();

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Finish()
    {
        var s = _env.Settings;
        if (VendorRoot != null) s.VendorPath = VendorRoot;
        if (ToolchainBin != null) s.ToolchainBin = ToolchainBin;
        _env.SaveSettings();
        ToolchainLocator.Rediscover();
        _env.Reload();
        Completed = true;
        _host.Close();
    }

    /// <summary>Closing the window cancels a running operation and detaches from the localizer.</summary>
    public void OnClosing()
    {
        _operation?.Cancel();
        Localizer.Instance.PropertyChanged -= OnLocalizerChanged;
    }

    private async Task RunAsync(Func<CancellationToken, Task> work, Action<Exception> onError)
    {
        using var cts = new CancellationTokenSource();
        _operation = cts;
        IsBusy = true;
        try
        {
            await work(cts.Token);
        }
        catch (OperationCanceledException)
        {
            onError(new OperationCanceledException(T("Setup.Cancelled")));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnsupportedInstallerException
                                      or UnauthorizedAccessException or HttpRequestException)
        {
            onError(e);
        }
        finally
        {
            _operation = null;
            IsBusy = false;
            SetProgress(() => "", 0, false);
            RefreshTexts();
        }
    }

    /// <summary>Forwards at most ~10 reports per second (the extractor reports every 64 KiB), plus phase changes.</summary>
    private sealed class ThrottledProgress<T>(IProgress<T> inner) : IProgress<T>
    {
        private long _last;
        private string? _lastPhase;

        public void Report(T value)
        {
            var phase = value switch { VendorImportProgress v => v.Phase, ToolchainInstallProgress t => t.Phase, _ => null };
            var now = Environment.TickCount64;
            if (phase == _lastPhase && now - Interlocked.Read(ref _last) < 100) return;
            _lastPhase = phase;
            Interlocked.Exchange(ref _last, now);
            inner.Report(value);
        }
    }
}
