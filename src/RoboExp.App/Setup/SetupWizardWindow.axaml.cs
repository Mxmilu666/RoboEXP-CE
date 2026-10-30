using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RoboExp.App.Services;

namespace RoboExp.App.Setup;

public partial class SetupWizardWindow : Window, ISetupWizardHost
{
    public SetupWizardWindow() => InitializeComponent();

    public SetupWizardWindow(AppEnvironment env) : this()
    {
        ViewModel = new SetupWizardViewModel(env, this);
        DataContext = ViewModel;
    }

    public SetupWizardViewModel? ViewModel { get; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        ViewModel?.OnClosing();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    public async Task<string?> PickInstallerAsync(string title, string filterName)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = ["*.exe", "*.EXE"] }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    void ISetupWizardHost.Close() => Close();
}
