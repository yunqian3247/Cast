using System.Text;

namespace cast.Core;

public static class TextCodec
{
    private static readonly Encoding Utf8;
    private static readonly Encoding Gbk;

    static TextCodec()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Utf8 = new UTF8Encoding(false, false);
        Gbk = Encoding.GetEncoding(936,
            EncoderFallback.ReplacementFallback,
            DecoderFallback.ReplacementFallback);
    }

    public static Encoding GetEncoding(TextEncodingKind kind)
    {
        return kind switch
        {
            TextEncodingKind.Gbk => Gbk,
            TextEncodingKind.Ascii => Encoding.ASCII,
            _ => Utf8
        };
    }

    public static byte[] Encode(string value, TextEncodingKind kind) => GetEncoding(kind).GetBytes(value ?? string.Empty);

    public static string Decode(ReadOnlySpan<byte> bytes, TextEncodingKind kind) => GetEncoding(kind).GetString(bytes);
}
