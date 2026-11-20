using Avalonia;

namespace RoboExp.App.Services;

internal static class StartupWindowBounds
{
    // Screen coordinates are physical pixels; client and native-frame sizes are logical pixels.
    public static (Size Client, Size Minimum, PixelPoint Position) Fit(
        PixelRect workArea, double scaling, Size preferred, Size minimum, Size frameInsets)
    {
        const double edge = 8;
        var available = new Size(
            Math.Max(1, workArea.Width / scaling - frameInsets.Width - 2 * edge),
            Math.Max(1, workArea.Height / scaling - frameInsets.Height - 2 * edge));
        var fittedMinimum = new Size(Math.Min(minimum.Width, available.Width), Math.Min(minimum.Height, available.Height));
        var client = new Size(
            Math.Clamp(preferred.Width, fittedMinimum.Width, available.Width),
            Math.Clamp(preferred.Height, fittedMinimum.Height, available.Height));
        var frameWidth = (int)Math.Ceiling((client.Width + frameInsets.Width) * scaling);
        var frameHeight = (int)Math.Ceiling((client.Height + frameInsets.Height) * scaling);
        return (client, fittedMinimum, new PixelPoint(
            workArea.X + Math.Max(0, (workArea.Width - frameWidth) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - frameHeight) / 2)));
    }
}
