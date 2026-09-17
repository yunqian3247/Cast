namespace cast.Core;

public sealed class PayloadEncodeException : FormatException
{
    public string ErrorCode { get; }

    public PayloadEncodeException(string errorCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}

public sealed record EncodedPayload(byte[] Bytes, string DisplayText);

public static class PayloadCodec
{
    public static EncodedPayload Encode(
        string? input,
        DataMode mode,
        TextEncodingKind encoding,
        LineEndingMode lineEnding,
        string? customLineEndingHex)
    {
        var value = input ?? string.Empty;
        byte[] bytes;
        try
        {
            bytes = mode == DataMode.Hex
                ? HexCodec.Parse(value)
                : TextCodec.Encode(value, encoding);
            bytes = [.. bytes, .. EncodeLineEnding(lineEnding, customLineEndingHex)];
        }
        catch (HexParseException ex)
        {
            throw new PayloadEncodeException(ErrorCodes.InvalidHex, ex.Message, ex);
        }

        return new EncodedPayload(bytes, value);
    }

    public static byte[] EncodeLineEnding(LineEndingMode mode, string? customHex) => mode switch
    {
        LineEndingMode.Cr => [0x0D],
        LineEndingMode.Lf => [0x0A],
        LineEndingMode.CrLf => [0x0D, 0x0A],
        LineEndingMode.CustomHex => HexCodec.Parse(customHex),
        _ => []
    };
}
