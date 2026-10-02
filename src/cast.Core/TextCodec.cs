using System.Text;

namespace cast.Core;

public static class TextCodec
{
    private static readonly Encoding Utf8;
    private static readonly Encoding Gbk;
    private static readonly Encoding StrictUtf8, StrictGbk, StrictAscii;

    static TextCodec()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Utf8 = new UTF8Encoding(false, false);
        Gbk = Encoding.GetEncoding(936,
            EncoderFallback.ReplacementFallback,
            DecoderFallback.ReplacementFallback);
        StrictUtf8 = new UTF8Encoding(false, true);
        StrictGbk = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
        StrictAscii = Encoding.GetEncoding(20127, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
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

    public static byte[] Encode(string value, TextEncodingKind kind)
    {
        var encoding = kind switch { TextEncodingKind.Ascii => StrictAscii, TextEncodingKind.Gbk => StrictGbk, _ => StrictUtf8 };
        try { return encoding.GetBytes(value ?? string.Empty); }
        catch (EncoderFallbackException ex)
        {
            var name = kind switch { TextEncodingKind.Ascii => "ASCII", TextEncodingKind.Gbk => "GBK", _ => "UTF-8" };
            throw new PayloadEncodeException("TX-INVALID-ENCODING",
                kind == TextEncodingKind.Utf8 ? $"第 {ex.Index + 1} 个字符包含非法 Unicode 代理项，请修正或移除该字符"
                    : $"第 {ex.Index + 1} 个字符无法使用 {name} 编码，请修正字符或选择 UTF-8", ex);
        }
    }

    public static string Decode(ReadOnlySpan<byte> bytes, TextEncodingKind kind) => GetEncoding(kind).GetString(bytes);
}
