using System.Text;

namespace serial.Core;

public static class TextCodec
{
    private static int _registered;

    public static Encoding GetEncoding(TextEncodingKind kind)
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        return kind switch
        {
            TextEncodingKind.Gbk => Encoding.GetEncoding(936,
                EncoderFallback.ReplacementFallback,
                DecoderFallback.ReplacementFallback),
            TextEncodingKind.Ascii => Encoding.ASCII,
            _ => new UTF8Encoding(false, false)
        };
    }

    public static byte[] Encode(string value, TextEncodingKind kind) => GetEncoding(kind).GetBytes(value ?? string.Empty);

    public static string Decode(ReadOnlySpan<byte> bytes, TextEncodingKind kind) => GetEncoding(kind).GetString(bytes);
}
