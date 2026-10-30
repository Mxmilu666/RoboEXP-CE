using System.Text;

namespace RoboExp.App.Services;

/// <summary>
/// Vendor and user C sources are GBK (code page 936, the original's <c>Encoding.Default</c> on Chinese Windows)
/// unless a BOM says otherwise. Saving keeps the file's encoding (and BOM); characters GBK cannot represent fail loudly
/// instead of turning into '?' on the controller LCD.
/// </summary>
public static class TextFileCodec
{
    public static Encoding Gbk { get; } = CreateGbk();

    private static Encoding CreateGbk()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
    }

    public static (string Text, Encoding Encoding) Read(string path) => Decode(File.ReadAllBytes(path));

    public static (string Text, Encoding Encoding) Decode(ReadOnlySpan<byte> bytes)
    {
        Encoding encoding;
        int bom;
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) { encoding = new UTF8Encoding(true, true); bom = 3; }
        else if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) { encoding = new UnicodeEncoding(false, true, true); bom = 2; }
        else if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) { encoding = new UnicodeEncoding(true, true, true); bom = 2; }
        else { encoding = Gbk; bom = 0; }
        return (encoding.GetString(bytes[bom..]), encoding);
    }

    /// <summary>Encodes with the preamble (BOM) of <paramref name="encoding"/>; GBK has none.</summary>
    /// <exception cref="EncoderFallbackException">The text contains characters the encoding cannot represent.</exception>
    public static byte[] Encode(string text, Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(text);
        if (preamble.Length == 0) return body;
        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }

    public static void Write(string path, string text, Encoding encoding)
    {
        var bytes = Encode(text, encoding);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }
}
