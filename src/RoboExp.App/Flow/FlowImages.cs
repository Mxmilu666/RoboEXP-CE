using Avalonia.Media.Imaging;
using RoboExp.App.Services;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;
using AvBitmap = Avalonia.Media.Imaging.Bitmap;

namespace RoboExp.App.Flow;

/// <summary>Decoding of stored icon bitmaps and the control-icon images new icons are given.</summary>
public static class FlowImages
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EncodedImage, AvBitmap?> Decoded = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EncodedImage, AvBitmap?> Grayed = new();

    /// <summary>The persisted <c>IconDraw.Image</c> as an Avalonia bitmap (cached per instance); null if undecodable.</summary>
    public static AvBitmap? Decode(EncodedImage? image)
    {
        if (image == null) return null;
        return Decoded.GetValue(image, img =>
        {
            try { return new AvBitmap(new MemoryStream(img.Data)); }
            catch (Exception) { return null; }
        });
    }

    /// <summary>
    /// IconDraw.Draw for icons not reached by code: every pixel becomes gray 0.7R + 0.2G + 0.1B (alpha dropped to opaque,
    /// as <c>Color.FromArgb(num, num, num)</c> did), after scaling to the icon size.
    /// </summary>
    public static AvBitmap? DecodeGray(EncodedImage? image, int width, int height)
    {
        if (image == null || width <= 0 || height <= 0) return null;
        return Grayed.GetValue(image, img =>
        {
            var source = Decode(img);
            if (source == null) return null;
            try
            {
                using var scaled = source.CreateScaledBitmap(new Avalonia.PixelSize(width, height), BitmapInterpolationMode.HighQuality);
                var stride = width * 4;
                var pixels = new byte[stride * height];
                var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
                try
                {
                    scaled.CopyPixels(new Avalonia.PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), pixels.Length, stride);
                }
                finally
                {
                    handle.Free();
                }
                var premul = scaled.AlphaFormat != Avalonia.Platform.AlphaFormat.Unpremul;
                var rgba = scaled.Format == Avalonia.Platform.PixelFormat.Rgba8888;
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
                    if (rgba) (r, b) = (b, r);
                    if (premul && a > 0 && a < 255)
                    {
                        r = r * 255 / a;
                        g = g * 255 / a;
                        b = b * 255 / a;
                    }
                    var gray = (byte)Math.Min(255, (int)(0.7 * r) + (int)(0.2 * g) + (int)(0.1 * b));
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = gray;
                    pixels[i + 3] = 255;
                }
                var wb = new WriteableBitmap(new Avalonia.PixelSize(width, height), new Avalonia.Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
                using (var fb = wb.Lock())
                {
                    for (var y = 0; y < height; y++)
                        System.Runtime.InteropServices.Marshal.Copy(pixels, y * stride, fb.Address + y * fb.RowBytes, stride);
                }
                return wb;
            }
            catch (Exception)
            {
                return null;
            }
        });
    }

    /// <summary>
    /// Image for a new control icon: the instance already used by an icon of the same type in the document (the original
    /// shares one CtrlIcons image per type), else the original's embedded <c>CtrlIcons/&lt;Type&gt;.ico</c> as PNG
    /// (BinaryFormatter stores in-memory/icon bitmaps as PNG).
    /// </summary>
    public static EncodedImage? ControlIconImage(IconType type, IEnumerable<GraphicObject> existing)
    {
        foreach (var go in existing)
            if (go.DrawObject is IconDraw { Image: { } img } icon && icon.IconType == type) return img;
        return EncodeControlIcon(type);
    }

    private static readonly Dictionary<IconType, EncodedImage?> Encoded = [];

    public static EncodedImage? EncodeControlIcon(IconType type)
    {
        if (Encoded.TryGetValue(type, out var cached)) return cached;
        EncodedImage? result = null;
        if (VendorImages.CtrlIcon(type.ToString()) is { } bmp)
        {
            var ms = new MemoryStream();
#pragma warning disable CS0618 // Save(Stream) writes PNG; the BitmapEncoderOptions overload is undocumented in 12.1
            bmp.Save(ms);
#pragma warning restore CS0618
            result = new EncodedImage(ms.ToArray());
        }
        Encoded[type] = result;
        return result;
    }
}
