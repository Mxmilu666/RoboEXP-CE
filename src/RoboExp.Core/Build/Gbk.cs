using System.Text;

namespace RoboExp.Core.Build;

/// <summary>Windows code page 936: vendor sources, generated .c files and the original's console output.</summary>
internal static class Gbk
{
    public static Encoding Encoding { get; } = Create();

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static Encoding Create()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936);
    }

    /// <summary>
    /// Decodes tool output line by line: valid UTF-8 lines (native toolchain messages, UTF-8 paths) stay UTF-8,
    /// anything else (gcc echoing GBK source lines) is decoded as GBK.
    /// </summary>
    public static string DecodeTolerant(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        while (!bytes.IsEmpty)
        {
            var nl = bytes.IndexOf((byte)'\n');
            var line = nl < 0 ? bytes : bytes[..(nl + 1)];
            try { sb.Append(StrictUtf8.GetString(line)); }
            catch (DecoderFallbackException) { sb.Append(Encoding.GetString(line)); }
            bytes = nl < 0 ? default : bytes[(nl + 1)..];
        }
        return sb.ToString();
    }
}
