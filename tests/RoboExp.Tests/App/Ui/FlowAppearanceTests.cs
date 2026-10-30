using Avalonia;
using RoboExp.App.Localization;
using RoboExp.App.Services;
using RoboExp.App.ViewModels;
using RoboExp.App.Views;

namespace RoboExp.Tests.App.Ui;

[Collection(nameof(LocalizerStateCollection))]
public class FlowAppearanceTests
{
    [Fact]
    public Task Custom_color_is_applied_only_on_ok_invalid_input_and_cancel_preserve_saved_choice() =>
        UiHarness.RunOnUiThreadAsync(() =>
        {
            var previousTheme = Application.Current!.RequestedThemeVariant;
            var previousLanguage = Localizer.Instance.Language;
            var dir = Directory.CreateTempSubdirectory("roboexp-background").FullName;
            var dialogs = new MainWindow();
            try
            {
                var path = Path.Combine(dir, "settings.json");
                var env = new AppEnvironment(new AppSettings { Language = previousLanguage }, path);
                var valid = new OptionsViewModel(env, dialogs)
                {
                    FlowBackgroundIndex = (int)FlowBackgroundMode.Custom,
                    FlowBackgroundColor = "#a1b2c3",
                };
                Assert.Equal(FlowBackgroundMode.Default, env.Settings.FlowBackground);
                valid.OkCommand.Execute(null);
                var saved = AppSettings.Load(path);
                Assert.Equal(FlowBackgroundMode.Custom, saved.FlowBackground);
                Assert.Equal("#A1B2C3", saved.FlowBackgroundColor);

                foreach (var invalidColor in new[] { "", "red", "#12345", "#GG0000", "#00112233" })
                {
                    var invalid = new OptionsViewModel(env, dialogs) { FlowBackgroundColor = invalidColor };
                    Assert.False(invalid.OkCommand.CanExecute(null));
                    invalid.OkCommand.Execute(null);
                    Assert.False(invalid.Accepted);
                    Assert.Equal("#A1B2C3", AppSettings.Load(path).FlowBackgroundColor);
                }

                var cancelled = new OptionsViewModel(env, dialogs)
                {
                    FlowBackgroundIndex = (int)FlowBackgroundMode.Classic,
                    FlowBackgroundColor = "#000000",
                };
                cancelled.CancelCommand.Execute(null);
                Assert.Equal(FlowBackgroundMode.Custom, env.Settings.FlowBackground);
                Assert.Equal("#A1B2C3", AppSettings.Load(path).FlowBackgroundColor);

                var classic = new OptionsViewModel(env, dialogs) { FlowBackgroundIndex = (int)FlowBackgroundMode.Classic };
                classic.OkCommand.Execute(null);
                Assert.Equal(FlowBackgroundMode.Classic, AppSettings.Load(path).FlowBackground);
                Assert.Equal("#A1B2C3", new OptionsViewModel(env, dialogs).FlowBackgroundColor);
            }
            finally
            {
                dialogs.Close();
                Application.Current.RequestedThemeVariant = previousTheme;
                Localizer.Instance.SetLanguage(previousLanguage);
                Directory.Delete(dir, recursive: true);
            }
            return Task.CompletedTask;
        });
}
