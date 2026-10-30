using Avalonia;
using System.Diagnostics;
using Avalonia.Threading;
using RoboExp.App.Localization;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using RoboExp.App.Services;
using RoboExp.App.Setup;
using RoboExp.App.ViewModels;
using RoboExp.App.Views;

namespace RoboExp.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            ThemeService.Apply(settings.Theme);
            var splash = new SplashWindow();
            var previousMode = desktop.ShutdownMode;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = splash;
            var handedOff = false;
            var closed = false;
            splash.Closed += (_, _) =>
            {
                closed = true;
                if (!handedOff) desktop.Shutdown();
            };
            splash.SetStatus(PortStrings.Get("Splash.Loading", settings.EffectiveLanguage(null)));
            splash.Opened += async (_, _) =>
            {
                var timer = Stopwatch.StartNew();
                try
                {
                    // Let the first splash frame render before loading vendor resources and libraries.
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    if (closed) return;
                    AppComposition.Register();
                    var env = new AppEnvironment(settings, AppSettings.DefaultPath);
                    var needsSetup = SetupWizardViewModel.IsSetupNeeded(env);
                    splash.SetStatus(Localizer.Instance.T(needsSetup ? "Port:Splash.Setup" : "Port:Splash.Opening"));
                    Window next;
                    if (needsSetup)
                    {
                        var wizard = new SetupWizardWindow(env);
                        wizard.Closed += (_, _) =>
                        {
                            var main = CreateMainWindow(desktop, env);
                            main.Show();
                            desktop.ShutdownMode = previousMode;
                        };
                        next = wizard;
                    }
                    else next = CreateMainWindow(desktop, env);
                    // A short minimum keeps the artwork/credit legible without simulating load progress.
                    var remaining = TimeSpan.FromMilliseconds(1200) - timer.Elapsed;
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
                    if (closed) return;
                    desktop.MainWindow = next;
                    next.Show();
                    handedOff = true;
                    splash.Close();
                    if (!needsSetup) desktop.ShutdownMode = previousMode;
                }
                catch (Exception e)
                {
                    if (!closed)
                    {
                        await MessageDialog.ShowAsync(splash, e.Message, "RoboExp CE", MessageDialog.Buttons.Ok);
                        splash.Close();
                    }
                }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static MainWindow CreateMainWindow(IClassicDesktopStyleApplicationLifetime desktop, AppEnvironment env)
    {
        var window = new MainWindow();
        var vm = new MainWindowViewModel(env, window);
        window.DataContext = vm;
        desktop.MainWindow = window;
        window.Opened += async (_, _) =>
        {
            foreach (var arg in desktop.Args ?? [])
                if (File.Exists(arg)) await vm.OpenPathAsync(arg);
        };
        return window;
    }
}
