using Avalonia;
using Avalonia.Styling;

namespace RoboExp.App.Services;

public static class ThemeService
{
    public static void Apply(AppTheme theme)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = theme switch
            {
                AppTheme.Light => ThemeVariant.Light,
                AppTheme.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
    }
}
