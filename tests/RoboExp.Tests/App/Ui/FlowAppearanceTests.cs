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

    [Fact]
    public Task Image_selection_is_previewed_and_only_changes_saved_background_on_ok() => UiHarness.RunAsync(ui =>
    {
        var env = ui.Vm.Env;
        var image = Path.Combine(ui.WorkDir, "background.png");
        using (var stream = File.Create(image))
        using (var surface = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(4, 4)))
            surface.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        var invalid = Path.Combine(ui.WorkDir, "invalid.png");
        File.WriteAllText(invalid, "not an image");

        var options = new OptionsViewModel(env, ui.Window);
        try
        {
            Assert.True(options.TrySetFlowImage(image));
            Assert.NotNull(options.FlowImagePreview);
            Assert.False(options.TrySetFlowImage(invalid));
            Assert.Equal("background.png", options.FlowImageName);
            Assert.Null(env.Settings.FlowBackgroundImagePath);
            options.OkCommand.Execute(null);
            Assert.Equal(image, AppSettings.Load(env.SettingsPath).FlowBackgroundImagePath);
        }
        finally { options.ReleaseFlowImagePreview(); }

        var remove = new OptionsViewModel(env, ui.Window);
        remove.RemoveFlowImageCommand.Execute(null);
        remove.CancelCommand.Execute(null);
        Assert.Equal(image, env.Settings.FlowBackgroundImagePath);
        var confirmed = new OptionsViewModel(env, ui.Window);
        confirmed.RemoveFlowImageCommand.Execute(null);
        confirmed.OkCommand.Execute(null);
        Assert.Null(AppSettings.Load(env.SettingsPath).FlowBackgroundImagePath);
        return Task.CompletedTask;
    });
}
