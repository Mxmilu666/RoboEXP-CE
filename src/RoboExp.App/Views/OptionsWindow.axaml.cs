using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RoboExp.App.Localization;
using RoboExp.App.Setup;
using RoboExp.App.ViewModels;

namespace RoboExp.App.Views;

public partial class OptionsWindow : Window
{
    public OptionsWindow() => InitializeComponent();

    protected override void OnOpened(EventArgs e)
    {
        if (DataContext is OptionsViewModel vm) vm.LoadFlowImagePreview();
        base.OnOpened(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is OptionsViewModel vm) vm.ReleaseFlowImagePreview();
        base.OnClosed(e);
    }

    private async void OnBrowseFlowImage(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OptionsViewModel vm) return;
        var loc = Localizer.Instance;
        var title = loc.T("Port:FlowBackground.Image");
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(title) { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp"] }],
        });
        if (!IsVisible || files.Count == 0) return;
        if (files[0].TryGetLocalPath() is not { } path || !vm.TrySetFlowImage(path))
            await MessageDialog.ShowAsync(this, loc.T("Port:FlowBackground.ImageError"), title, MessageDialog.Buttons.Ok);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is OptionsViewModel vm)
        {
            vm.CloseRequested += (_, _) => Close();
            vm.SetupWizardRequested += async (_, _) =>
            {
                var wizard = new SetupWizardWindow(vm.Env) { WindowStartupLocation = WindowStartupLocation.CenterOwner };
                await wizard.ShowDialog(this);
                if (wizard.ViewModel?.Completed == true) vm.ReloadPaths();
            };
        }
    }
}
