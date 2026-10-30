using System.Buffers.Binary;

namespace RoboExp.Core.Model;

/// <summary>Replacement for <c>System.Drawing.Point</c> (serialized members <c>x</c>, <c>y</c>).</summary>
public record struct Point(int X, int Y)
{
    public static readonly Point Empty = default;
}

/// <summary>Replacement for <c>System.Drawing.Size</c> (serialized members <c>width</c>, <c>height</c>).</summary>
public record struct Size(int Width, int Height)
{
    public static readonly Size Empty = default;
}

/// <summary>
/// Replacement for <c>System.Drawing.Color</c>, keeping its serialized state verbatim
/// (<c>name</c>, <c>value</c>, <c>knownColor</c>, <c>state</c>).
/// </summary>
/// <remarks>
/// A known color (e.g. <c>Color.Red</c>, the default line color) stores only <see cref="KnownColor"/>; its ARGB comes
/// from the System.Drawing known-color table (<see cref="ToArgb"/>).
/// </remarks>
public readonly record struct Color(string? Name, long Value, short KnownColor, short State)
{
    public const short StateKnownColorValid = 0x0001;
    public const short StateArgbValueValid = 0x0002;
    public const short StateNameValid = 0x0008;

    // System.Drawing.KnownColor values used by the original model defaults.
    public const short KnownColorBlack = 35;
    public const short KnownColorRed = 141;
    public const short KnownColorWhite = 164;

    public static readonly Color Empty = default;
    public static Color Red => FromKnownColor(KnownColorRed);
    public static Color White => FromKnownColor(KnownColorWhite);
    public static Color Black => FromKnownColor(KnownColorBlack);

    /// <summary>Same encoding as <c>System.Drawing.Color.FromArgb(int)</c>.</summary>
    public static Color FromArgb(int argb) => new(null, argb & 0xFFFFFFFFL, 0, StateArgbValueValid);

    public static Color FromArgb(byte a, byte r, byte g, byte b) => FromArgb(a << 24 | r << 16 | g << 8 | b);

    /// <summary>Same encoding as <c>System.Drawing.Color.FromKnownColor</c>.</summary>
    public static Color FromKnownColor(short knownColor) => new(null, 0, knownColor, StateKnownColorValid);

    public bool IsEmpty => State == 0;
    public bool IsKnownColor => (State & StateKnownColorValid) != 0;

    public int ToArgb()
    {
        if ((State & StateArgbValueValid) != 0) return unchecked((int)Value);
        if (IsKnownColor && KnownColor >= 1 && KnownColor <= KnownColorArgb.Length) return unchecked((int)KnownColorArgb[KnownColor - 1]);
        return 0;
    }

    public byte A => (byte)(ToArgb() >> 24);
    public byte R => (byte)(ToArgb() >> 16);
    public byte G => (byte)(ToArgb() >> 8);
    public byte B => (byte)ToArgb();

    // System.Drawing.KnownColor 1..174 (ActiveBorder .. MenuHighlight). System colors use the classic Windows defaults.
    private static readonly uint[] KnownColorArgb =
    [
        0xFFD4D0C8, 0xFF0054E3, 0xFFFFFFFF, 0xFF808080, 0xFFECE9D8, 0xFFACA899, 0xFF716F64, 0xFFF1EFE2,
        0xFFFFFFFF, 0xFF000000, 0xFF004E98, 0xFFACA899, 0xFF316AC5, 0xFFFFFFFF, 0xFF000080, 0xFFD4D0C8,
        0xFF7A96DF, 0xFFD8E4F8, 0xFFFFFFE1, 0xFF000000, 0xFFFFFFFF, 0xFF000000, 0xFFD4D0C8, 0xFFFFFFFF,
        0xFF000000, 0xFF000000, 0x00FFFFFF, 0xFFF0F8FF, 0xFFFAEBD7, 0xFF00FFFF, 0xFF7FFFD4, 0xFFF0FFFF,
        0xFFF5F5DC, 0xFFFFE4C4, 0xFF000000, 0xFFFFEBCD, 0xFF0000FF, 0xFF8A2BE2, 0xFFA52A2A, 0xFFDEB887,
        0xFF5F9EA0, 0xFF7FFF00, 0xFFD2691E, 0xFFFF7F50, 0xFF6495ED, 0xFFFFF8DC, 0xFFDC143C, 0xFF00FFFF,
        0xFF00008B, 0xFF008B8B, 0xFFB8860B, 0xFFA9A9A9, 0xFF006400, 0xFFBDB76B, 0xFF8B008B, 0xFF556B2F,
        0xFFFF8C00, 0xFF9932CC, 0xFF8B0000, 0xFFE9967A, 0xFF8FBC8F, 0xFF483D8B, 0xFF2F4F4F, 0xFF00CED1,
        0xFF9400D3, 0xFFFF1493, 0xFF00BFFF, 0xFF696969, 0xFF1E90FF, 0xFFB22222, 0xFFFFFAF0, 0xFF228B22,
        0xFFFF00FF, 0xFFDCDCDC, 0xFFF8F8FF, 0xFFFFD700, 0xFFDAA520, 0xFF808080, 0xFF008000, 0xFFADFF2F,
        0xFFF0FFF0, 0xFFFF69B4, 0xFFCD5C5C, 0xFF4B0082, 0xFFFFFFF0, 0xFFF0E68C, 0xFFE6E6FA, 0xFFFFF0F5,
        0xFF7CFC00, 0xFFFFFACD, 0xFFADD8E6, 0xFFF08080, 0xFFE0FFFF, 0xFFFAFAD2, 0xFFD3D3D3, 0xFF90EE90,
        0xFFFFB6C1, 0xFFFFA07A, 0xFF20B2AA, 0xFF87CEFA, 0xFF778899, 0xFFB0C4DE, 0xFFFFFFE0, 0xFF00FF00,
        0xFF32CD32, 0xFFFAF0E6, 0xFFFF00FF, 0xFF800000, 0xFF66CDAA, 0xFF0000CD, 0xFFBA55D3, 0xFF9370DB,
        0xFF3CB371, 0xFF7B68EE, 0xFF00FA9A, 0xFF48D1CC, 0xFFC71585, 0xFF191970, 0xFFF5FFFA, 0xFFFFE4E1,
        0xFFFFE4B5, 0xFFFFDEAD, 0xFF000080, 0xFFFDF5E6, 0xFF808000, 0xFF6B8E23, 0xFFFFA500, 0xFFFF4500,
        0xFFDA70D6, 0xFFEEE8AA, 0xFF98FB98, 0xFFAFEEEE, 0xFFDB7093, 0xFFFFEFD5, 0xFFFFDAB9, 0xFFCD853F,
        0xFFFFC0CB, 0xFFDDA0DD, 0xFFB0E0E6, 0xFF800080, 0xFFFF0000, 0xFFBC8F8F, 0xFF4169E1, 0xFF8B4513,
        0xFFFA8072, 0xFFF4A460, 0xFF2E8B57, 0xFFFFF5EE, 0xFFA0522D, 0xFFC0C0C0, 0xFF87CEEB, 0xFF6A5ACD,
        0xFF708090, 0xFFFFFAFA, 0xFF00FF7F, 0xFF4682B4, 0xFFD2B48C, 0xFF008080, 0xFFD8BFD8, 0xFFFF6347,
        0xFF40E0D0, 0xFFEE82EE, 0xFFF5DEB3, 0xFFFFFFFF, 0xFFF5F5F5, 0xFFFFFF00, 0xFF9ACD32, 0xFFF0F0F0,
        0xFFFFFFFF, 0xFFA0A0A0, 0xFFB9D1EA, 0xFFD7E4F2, 0xFFF0F0F0, 0xFF3399FF,
    ];
}

public enum ImageFormat { Unknown, Png, Bmp, Gif, Jpeg, Icon }

/// <summary>
/// Replacement for a serialized <c>System.Drawing.Bitmap</c>: the encoded image bytes stored under member <c>Data</c>
/// (PNG for almost all vendor files; BMP and GIF also occur). Bytes are kept verbatim so files round-trip exactly.
/// Instances are shared by reference where the original shared one <c>Image</c> (e.g. the standard flowchart icons).
/// </summary>
public sealed class EncodedImage
{
    public EncodedImage(byte[] data) => Data = data;

    public byte[] Data { get; set; }

    public ImageFormat Format => Data switch
    {
        [0x89, 0x50, 0x4E, 0x47, ..] => ImageFormat.Png,
        [0x42, 0x4D, ..] => ImageFormat.Bmp,
        [0x47, 0x49, 0x46, ..] => ImageFormat.Gif,
        [0xFF, 0xD8, ..] => ImageFormat.Jpeg,
        [0x00, 0x00, 0x01, 0x00, ..] => ImageFormat.Icon,
        _ => ImageFormat.Unknown,
    };

    /// <summary>Pixel size read from the encoded header, or null when the format is not recognised.</summary>
    public Size? PixelSize
    {
        get
        {
            var d = Data;
            switch (Format)
            {
                case ImageFormat.Png when d.Length >= 24:
                    return new Size(BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(20)));
                case ImageFormat.Bmp when d.Length >= 26:
                    return new Size(BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(18)), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(22))));
                case ImageFormat.Gif when d.Length >= 10:
                    return new Size(BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(6)), BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(8)));
                case ImageFormat.Icon when d.Length >= 8:
                    return new Size(d[6] == 0 ? 256 : d[6], d[7] == 0 ? 256 : d[7]);
                default:
                    return null;
            }
        }
    }
}
