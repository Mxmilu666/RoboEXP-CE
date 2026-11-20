using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace RoboExp.App.Services;

internal static class FlowBackgroundImage
{
    internal const double Opacity = 0.30;

    internal static Bitmap? Load(string? path, int maxSide = 2048)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            int width, height;
            using (var input = new SKManagedStream(stream, false))
            using (var codec = SKCodec.Create(input))
            {
                if (codec == null) return null;
                (width, height) = (codec.Info.Width, codec.Info.Height);
            }
            if (width <= 0 || height <= 0) return null;
            stream.Position = 0;
            if (Math.Max(width, height) <= maxSide) return new Bitmap(stream);
            return width >= height ? Bitmap.DecodeToWidth(stream, maxSide) : Bitmap.DecodeToHeight(stream, maxSide);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    internal static ImageBrush CreateBrush(Bitmap image) => new(image) { Stretch = Stretch.UniformToFill, Opacity = Opacity };
}
