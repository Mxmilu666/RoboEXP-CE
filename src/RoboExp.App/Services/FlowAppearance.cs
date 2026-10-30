using Avalonia.Media;

namespace RoboExp.App.Services;

internal static class FlowAppearance
{
    // Opaque RGB only: a transparent canvas would depend on whichever pane sits behind it.
    public static bool TryParseCustomColor(string? text, out Color color)
    {
        text = text?.Trim();
        color = default;
        return text is { Length: 7 } && text[0] == '#' &&
               text.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0 && Color.TryParse(text, out color);
    }

    public static Color Background(AppSettings? settings, bool dark, int classicArgb)
    {
        if (settings?.FlowBackground == FlowBackgroundMode.Classic) return Color.FromUInt32(unchecked((uint)classicArgb));
        if (settings?.FlowBackground == FlowBackgroundMode.Custom && TryParseCustomColor(settings.FlowBackgroundColor, out var color)) return color;
        return Color.Parse(dark ? "#202020" : "#FAFAFA");
    }

    public static bool UseDarkInk(Color background)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(background.R) + 0.7152 * Linear(background.G) + 0.0722 * Linear(background.B) > 0.179;
    }
}
