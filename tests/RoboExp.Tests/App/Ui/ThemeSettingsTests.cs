using Avalonia;
using Avalonia.Styling;
using RoboExp.App.Localization;
using RoboExp.App.Services;
using RoboExp.App.ViewModels;
using RoboExp.App.Views;
using RoboExp.Core.Library;

namespace RoboExp.Tests.App.Ui;

[Collection(nameof(LocalizerStateCollection))]
public class ThemeSettingsTests
{
    [Theory]
    [InlineData(AppTheme.System)]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void Theme_round_trips(AppTheme theme)
    {
        var dir = Directory.CreateTempSubdirectory("roboexp-theme").FullName;
        try
        {
            var path = Path.Combine(dir, "settings.json");
            new AppSettings { Theme = theme }.Save(path);
            Assert.Equal(theme, AppSettings.Load(path).Theme);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Missing_theme_defaults_to_system()
    {
        var dir = Directory.CreateTempSubdirectory("roboexp-theme").FullName;
        try
        {
            var path = Path.Combine(dir, "settings.json");
            Assert.Equal(AppTheme.System, AppSettings.Load(path).Theme);
            File.WriteAllText(path, "{}");
            Assert.Equal(AppTheme.System, AppSettings.Load(path).Theme);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public Task Ok_applies_and_persists_each_choice_cancel_leaves_theme_unchanged() =>
        UiHarness.RunOnUiThreadAsync(() =>
        {
            var app = Application.Current!;
            var previousTheme = app.RequestedThemeVariant;
            var previousLanguage = Localizer.Instance.Language;
            var dir = Directory.CreateTempSubdirectory("roboexp-theme").FullName;
            var dialogs = new MainWindow();
            try
            {
                var path = Path.Combine(dir, "settings.json");
                var env = new AppEnvironment(new AppSettings { Language = previousLanguage }, path);
                var variants = new[] { ThemeVariant.Default, ThemeVariant.Light, ThemeVariant.Dark };
                foreach (var theme in new[] { AppTheme.System, AppTheme.Light, AppTheme.Dark })
                {
                    var vm = new OptionsViewModel(env, dialogs);
                    Assert.Equal((int)env.Settings.Theme, vm.ThemeIndex);
                    var before = app.RequestedThemeVariant;
                    vm.ThemeIndex = (int)theme;
                    Assert.Equal(before, app.RequestedThemeVariant);
                    vm.OkCommand.Execute(null);
                    Assert.True(vm.Accepted);
                    Assert.Equal(theme, env.Settings.Theme);
                    Assert.Equal(theme, AppSettings.Load(path).Theme);
                    Assert.Equal(variants[(int)theme], app.RequestedThemeVariant);
                }

                var cancelled = new OptionsViewModel(env, dialogs) { ThemeIndex = (int)AppTheme.Light };
                cancelled.CancelCommand.Execute(null);
                Assert.False(cancelled.Accepted);
                Assert.Equal(AppTheme.Dark, env.Settings.Theme);
                Assert.Equal(AppTheme.Dark, AppSettings.Load(path).Theme);
                Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
            }
            finally
            {
                dialogs.Close();
                app.RequestedThemeVariant = previousTheme;
                Localizer.Instance.SetLanguage(previousLanguage);
                Directory.Delete(dir, recursive: true);
            }
            return Task.CompletedTask;
        });

}
