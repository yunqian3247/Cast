namespace cast.Core;

public sealed class HexParseException : FormatException
{
    public int Position { get; }

    public HexParseException(int position, string message)
        : base($"{message} (位置 {position})")
    {
        Position = position;
    }
}

public static class HexCodec
{
    public static byte[] Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        var result = new List<byte>();
        var highNibble = -1;
        var highPosition = -1;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (char.IsWhiteSpace(c) || c == ',')
            {
                continue;
            }

            if (c == '0' && i + 1 < input.Length && (input[i + 1] == 'x' || input[i + 1] == 'X'))
            {
                if (highNibble >= 0)
                {
                    throw new HexParseException(i, "十六进制前缀必须位于字节开头");
                }

                if (i + 2 >= input.Length || HexValue(input[i + 2]) < 0)
                {
                    throw new HexParseException(i, "十六进制前缀后必须包含数据");
                }

                i++;
                continue;
            }

            var nibble = HexValue(c);
            if (nibble < 0)
            {
                throw new HexParseException(i, $"非法十六进制字符 '{c}'");
            }

            if (highNibble < 0)
            {
                highNibble = nibble;
                highPosition = i;
            }
            else
            {
                result.Add((byte)((highNibble << 4) | nibble));
                highNibble = -1;
            }
        }

        if (highNibble >= 0)
        {
            throw new HexParseException(highPosition, "十六进制数据必须包含完整字节");
        }

        return [.. result];
    }

    public static bool TryParse(string? input, out byte[] bytes, out string? error)
    {
        try
        {
            bytes = Parse(input);
            error = null;
            return true;
        }
        catch (HexParseException ex)
        {
            bytes = [];
            error = ex.Message;
            return false;
        }
    }

    public static string Format(ReadOnlySpan<byte> bytes, string separator = " ")
    {
        return string.Join(separator, bytes.ToArray().Select(static b => b.ToString("X2")));
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
    };
}
