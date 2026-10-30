using Avalonia.Controls;
using RoboExp.App.Setup;
using RoboExp.App.ViewModels;

namespace RoboExp.App.Views;

public partial class OptionsWindow : Window
{
    public OptionsWindow() => InitializeComponent();

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
