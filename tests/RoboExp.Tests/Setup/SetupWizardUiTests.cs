using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RoboExp.App.Services;
using RoboExp.App.Setup;
using RoboExp.Tests.App.Ui;
using RoboExp.Tests.Build;

namespace RoboExp.Tests.Setup;

/// <summary>
/// Drives the real <see cref="SetupWizardWindow"/> in the shared headless Avalonia session with pointer/keyboard
/// input (native file pickers are bypassed through the wizard's path boxes).
/// </summary>
public class SetupWizardUiTests
{
    private sealed class Driver(Window window)
    {
        public void Pump()
        {
            for (var i = 0; i < 3; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
            Dispatcher.UIThread.RunJobs();
        }

        public async Task SettleAsync(int ms = 50)
        {
            Pump();
            await Task.Delay(ms);
            Pump();
        }

        public T Find<T>(string name) where T : Control =>
            window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

        public void Click(Control control)
        {
            Assert.True(control.IsEffectivelyVisible && control.IsEffectivelyEnabled, $"{control.Name} is not clickable");
            var p = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            window.MouseMove(p, RawInputModifiers.None);
            window.MouseDown(p, MouseButton.Left, RawInputModifiers.None);
            Pump();
            window.MouseUp(p, MouseButton.Left, RawInputModifiers.None);
            Pump();
        }

        public void TypeInto(TextBox box, string text)
        {
            Click(box);
            window.KeyTextInput(text);
            Pump();
        }

        public void Screenshot(string? dir, string name)
        {
            if (string.IsNullOrEmpty(dir)) return;
            Pump();
            var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame rendered");
            Directory.CreateDirectory(dir);
            frame.Save(Path.Combine(dir, name + ".png"));
        }
    }

    private static string NewTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [UnixFact]
    public async Task Wizard_NavigatesWithClicks_ValidatesPaths_AndFinishSavesSettings()
    {
        var work = NewTempDir("roboexp-wizard-");
        try
        {
            // Fake toolchain bin (only the presence of arm-none-eabi-gcc is checked).
            var bin = Path.Combine(work, "tc", "bin");
            Directory.CreateDirectory(bin);
            await File.WriteAllTextAsync(Path.Combine(bin, "arm-none-eabi-gcc"), "#!/bin/sh\n");
            var settingsPath = Path.Combine(work, "settings.json");

            await UiHarness.RunOnUiThreadAsync(async () =>
            {
                var env = new AppEnvironment(new AppSettings { Language = RoboExp.Core.Library.UiLanguage.Default }, settingsPath);
                var wizard = new SetupWizardWindow(env);
                wizard.Show();
                var ui = new Driver(wizard);
                await ui.SettleAsync();
                var vm = wizard.ViewModel!;
                Assert.Equal(SetupStep.Welcome, vm.Step);
                Assert.False(ui.Find<Button>("BackButton").IsEffectivelyEnabled);

                ui.Click(ui.Find<Button>("NextButton"));
                Assert.Equal(SetupStep.Vendor, vm.Step);
                ui.TypeInto(ui.Find<TextBox>("VendorPathBox"), Path.Combine(work, "nothing-here"));
                ui.Click(ui.Find<Button>("UseVendorPathButton"));
                await ui.SettleAsync();
                Assert.Contains("Not a RoboEXP install", vm.VendorStatusText);

                ui.Click(ui.Find<Button>("NextButton"));
                Assert.Equal(SetupStep.Toolchain, vm.Step);
                ui.TypeInto(ui.Find<TextBox>("ToolchainPathBox"), bin);
                ui.Click(ui.Find<Button>("UseToolchainPathButton"));
                await ui.SettleAsync();
                Assert.Equal(Path.GetFullPath(bin), vm.ToolchainBin);

                ui.Click(ui.Find<Button>("BackButton"));
                Assert.Equal(SetupStep.Vendor, vm.Step);
                ui.Click(ui.Find<Button>("NextButton"));
                ui.Click(ui.Find<Button>("NextButton"));
                Assert.Equal(SetupStep.Finish, vm.Step);
                Assert.False(ui.Find<Button>("NextButton").IsVisible);
                ui.Click(ui.Find<Button>("FinishButton"));
                await ui.SettleAsync();
                Assert.True(vm.Completed);
                Assert.False(wizard.IsVisible);
            });

            Assert.Equal(Path.GetFullPath(bin), AppSettings.Load(settingsPath).ToolchainBin);
        }
        finally
        {
            RoboExp.Core.Build.ToolchainLocator.Rediscover();
            Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>
    /// Full first-run flow with real data: imports the installer through the path box and downloads the Arm toolchain.
    /// Manual evidence run only (network, ~150 MB): set ROBOEXP_SETUP_EVIDENCE=&lt;screenshot dir&gt; plus fresh
    /// ROBOEXP_DATA_DIR / ROBOEXP_CONFIG_DIR for the test process.
    /// </summary>
    [InstallerFact]
    public async Task Wizard_FullFirstRun_Evidence()
    {
        var shots = Environment.GetEnvironmentVariable("ROBOEXP_SETUP_EVIDENCE");
        if (string.IsNullOrEmpty(shots)) return; // opt-in
        Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RoboExp.Core.Install.DataDirectory.EnvVar)));
        Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AppSettings.ConfigDirEnvVar)));

        await UiHarness.RunOnUiThreadAsync(async () =>
        {
            var settingsPath = AppSettings.DefaultPath;
            var env = new AppEnvironment(AppSettings.Load(settingsPath), settingsPath);
            var wizard = new SetupWizardWindow(env);
            wizard.Show();
            var ui = new Driver(wizard);
            var vm = wizard.ViewModel!;
            await ui.SettleAsync();
            ui.Screenshot(shots, "01-welcome");

            async Task WaitIdle(string midShot)
            {
                var shot = false;
                var deadline = DateTime.UtcNow.AddMinutes(15);
                while (vm.IsBusy)
                {
                    Assert.True(DateTime.UtcNow < deadline, "operation timed out");
                    await ui.SettleAsync(250);
                    if (!shot && vm.Progress > 30) { ui.Screenshot(shots, midShot); shot = true; }
                }
                await ui.SettleAsync();
            }

            ui.Click(ui.Find<Button>("NextButton"));
            ui.Screenshot(shots, "02-vendor");
            ui.TypeInto(ui.Find<TextBox>("VendorPathBox"), InnoExtractorTests.InstallerPath!);
            ui.Click(ui.Find<Button>("UseVendorPathButton"));
            await WaitIdle("03-vendor-extracting");
            ui.Screenshot(shots, "04-vendor-imported");
            Assert.StartsWith(RoboExp.Core.Install.DataDirectory.VendorRoot, vm.VendorRoot);

            ui.Click(ui.Find<Button>("NextButton"));
            ui.Screenshot(shots, "05-toolchain");
            ui.Click(ui.Find<Button>("DownloadButton"));
            await WaitIdle("06-toolchain-downloading");
            ui.Screenshot(shots, "07-toolchain-installed");
            Assert.StartsWith(RoboExp.Core.Install.DataDirectory.ToolchainsRoot, vm.ToolchainBin);

            ui.Click(ui.Find<Button>("NextButton"));
            ui.Screenshot(shots, "08-finish");
            ui.Click(ui.Find<Button>("FinishButton"));
            await ui.SettleAsync();
            Assert.True(vm.Completed);
        });

        var saved = AppSettings.Load(AppSettings.DefaultPath);
        Assert.StartsWith(RoboExp.Core.Install.DataDirectory.VendorRoot, saved.VendorPath);
        Assert.StartsWith(RoboExp.Core.Install.DataDirectory.ToolchainsRoot, saved.ToolchainBin);
    }
}
